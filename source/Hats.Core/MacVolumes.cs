using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Hats.Core;

public sealed record MacVolume(string Root, string Name, string Device, string Uuid, string FileSystem, long TotalBytes, long FreeBytes)
{
    public override string ToString() => $"{Name}  ·  {TotalBytes / 1073741824d:N0} GB  ·  {(FileSystem == "msdos" ? "FAT32" : "exFAT")}";
}

public static class MacVolumes
{
    public static MacVolume Parse(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
        var entries = XDocument.Load(reader).Root?.Element("dict")?.Elements().ToArray() ?? throw new IOException("macOS did not return disk information.");
        var fields = new Dictionary<string, XElement>();
        for (int i = 0; i < entries.Length; i += 2)
        {
            if (i + 1 >= entries.Length || entries[i].Name != "key" || !fields.TryAdd(entries[i].Value, entries[i + 1])) throw new IOException("Ambiguous disk information.");
        }
        string Text(string name) => fields.TryGetValue(name, out var value) && value.Name == "string" ? value.Value : "";
        bool? Flag(string name) => fields.TryGetValue(name, out var value) ? value.Name.LocalName switch { "true" => true, "false" => false, _ => null } : null;
        long Number(string name) => fields.TryGetValue(name, out var value) && value.Name == "integer" && long.TryParse(value.Value, out var number) ? number : 0;
        var mount = Text("MountPoint"); var device = Text("DeviceIdentifier"); var fs = Text("FilesystemType").ToLowerInvariant();
        var uuid = Text("VolumeUUID");
        if (!mount.StartsWith("/Volumes/", StringComparison.Ordinal) || mount[9..].Length == 0 || mount[9..].Contains('/') || mount[9..] is "." or "..") throw new IOException("Select a mounted SD card under /Volumes.");
        if (!Regex.IsMatch(device, "^disk[0-9]+(?:s[0-9]+)*$", RegexOptions.CultureInvariant)) throw new IOException("The disk identifier is not recognised.");
        if (Text("VirtualOrPhysical") != "Physical" || !(Flag("Internal") == false || Flag("RemovableMedia") == true)) throw new IOException("Only an external or removable physical SD card can be updated.");
        if (Flag("ReadOnlyVolume") != false || Flag("ReadOnlyMedia") != false) throw new IOException("The SD card is read-only or its write status is unknown.");
        if (fs is not ("msdos" or "exfat")) throw new IOException("The SD card must already use FAT32 or exFAT.");
        if (fs == "msdos" && !Text("FileSystemPersonality").Contains("FAT32", StringComparison.OrdinalIgnoreCase)) throw new IOException("Use a FAT32 or exFAT SD card.");
        if (uuid.Length == 0) throw new IOException("macOS could not identify this card reliably. Reconnect it and try again.");
        long total = Number("TotalSize"); if (total == 0) total = Number("Size");
        long free = Number("VolumeFreeSpace");
        if (total <= 0 || free < 0 || free > total) throw new IOException("The SD card's reported capacity is invalid.");
        return new(mount, string.IsNullOrWhiteSpace(Text("VolumeName")) ? mount[9..] : Text("VolumeName"), device, uuid, fs, total, free);
    }
    public static async Task<MacVolume> InspectAsync(string path, CancellationToken cancel = default)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("SD card access is available on macOS.");
        path = Paths.Root(path);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var process = new Process { StartInfo = new ProcessStartInfo("/usr/sbin/diskutil") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        process.StartInfo.ArgumentList.Add("info"); process.StartInfo.ArgumentList.Add("-plist"); process.StartInfo.ArgumentList.Add(path);
        process.Start();
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token); var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token); var xml = await stdout; await stderr;
            if (process.ExitCode != 0) throw new IOException("macOS could not read this SD card. Reconnect it and choose Refresh.");
            var volume = Parse(xml);
            if (!volume.Root.Equals(path, StringComparison.Ordinal)) throw new IOException("Choose the root of the SD card, not a subfolder.");
            return volume;
        }
        finally { if (!process.HasExited) process.Kill(); }
    }
    public static async Task<MacVolume[]> ScanAsync()
    {
        if (!OperatingSystem.IsMacOS() || !Directory.Exists("/Volumes")) return [];
        var volumes = new List<MacVolume>();
        foreach (var path in Directory.EnumerateDirectories("/Volumes"))
            try { volumes.Add(await InspectAsync(path)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException or XmlException) { }
        return volumes.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static void SameCard(MacVolume expected, MacVolume actual)
    {
        if (expected.Root != actual.Root || expected.Device != actual.Device || expected.Uuid != actual.Uuid || expected.FileSystem != actual.FileSystem || expected.TotalBytes != actual.TotalBytes)
            throw new IOException("The selected SD card changed. Refresh and select it again.");
    }
}

public sealed record MountIdentity(long Fsid, string Mount, string Device, string FileSystem, uint Flags)
{
    public static MountIdentity Decode(byte[] buffer)
    {
        if (buffer.Length < 2168) throw new IOException("Incomplete macOS mount information.");
        string Text(int offset, int count) { var end = Array.IndexOf(buffer, (byte)0, offset, count); if (end < 0) throw new IOException("Invalid macOS mount information."); return new UTF8Encoding(false, true).GetString(buffer, offset, end - offset); }
        return new(BitConverter.ToInt64(buffer, 48), Text(88, 1024), Text(1112, 1024), Text(72, 16), BitConverter.ToUInt32(buffer, 64));
    }
    public void EnsureSame(MountIdentity current)
    {
        if (Fsid != current.Fsid || Mount != current.Mount || Device != current.Device || FileSystem != current.FileSystem || (current.Flags & 1) != 0)
            throw new IOException("The SD card was disconnected, changed or became read-only. Keep the backup for recovery.");
    }
}

public sealed class MacVolumeGuard
{
    readonly MacVolume volume;
    readonly MountIdentity mounted;
    public MacVolumeGuard(MacVolume volume)
    {
        this.volume = volume; mounted = Read(volume.Root);
        if (mounted.Mount != volume.Root || mounted.Device != "/dev/" + volume.Device || mounted.FileSystem != volume.FileSystem || (mounted.Flags & 1) != 0)
            throw new IOException("The SD card mount changed. Refresh and select it again.");
    }
    public void Validate()
    {
        if (!Directory.Exists(volume.Root)) throw new IOException("The SD card was disconnected.");
        Paths.Root(volume.Root); mounted.EnsureSame(Read(volume.Root));
    }
    public static MountIdentity Read(string path)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        var buffer = new byte[2168];
        // Darwin's statfs64 layout. Intel exports the INODE64 name; arm64 only has the 64-bit ABI.
        int result = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? StatFsArm(path, buffer) : StatFsIntel(path, buffer);
        if (result != 0) throw new IOException("The SD card mount is unavailable.");
        return MountIdentity.Decode(buffer);
    }
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "statfs", SetLastError = true)]
    static extern int StatFsArm([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] buffer);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "statfs$INODE64", SetLastError = true)]
    static extern int StatFsIntel([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] buffer);
}
