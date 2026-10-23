using System.IO.Compression;

namespace Hats.Core;

public static class ArchiveService
{
    public static void Extract(string archive, string destination, Action<double>? progress = null, CancellationToken cancel = default)
    {
        destination = Paths.Root(destination);
        if (Paths.Exists(destination)) throw new IOException("Use an empty, new extraction folder.");
        using var zip = ZipFile.OpenRead(archive);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            cancel.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/').TrimEnd('/'); Paths.ValidateRelative(name);
            var type = (entry.ExternalAttributes >> 16) & 0xF000;
            if (type == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("The ZIP contains a linked path.");
            if (!seen.Add(name)) throw new InvalidDataException($"Duplicate path in ZIP: {name}");
            if (!entry.FullName.EndsWith('/') && !entry.FullName.EndsWith('\\')) files.Add(name);
            total = checked(total + entry.Length);
        }
        foreach (var name in seen)
        {
            var parts = name.Split('/');
            for (var i = 1; i < parts.Length; i++) if (files.Contains(string.Join('/', parts.Take(i)))) throw new InvalidDataException("The ZIP contains a file/folder conflict.");
        }
        if (Paths.FreeSpace(destination) < total + 16L * 1048576) throw new IOException("Not enough temporary disk space to unpack HATS.");
        Directory.CreateDirectory(destination); long done = 0;
        var buffer = new byte[1048576];
        foreach (var entry in zip.Entries)
        {
            cancel.ThrowIfCancellationRequested(); var name = entry.FullName.Replace('\\', '/').TrimEnd('/'); var target = Paths.Checked(destination, name);
            if (!files.Contains(name)) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open(); using var output = new FileStream(target, FileMode.CreateNew);
            long count = 0; uint crc = 0xFFFFFFFF;
            while (true)
            {
                cancel.ThrowIfCancellationRequested(); int n = input.Read(buffer); if (n == 0) break;
                count += n; if (count > entry.Length) throw new InvalidDataException("ZIP entry exceeds its declared size.");
                crc = Crc32.Update(crc, buffer.AsSpan(0, n)); output.Write(buffer, 0, n); done += n; progress?.Invoke(done * 100d / Math.Max(1, total));
            }
            if (count != entry.Length || ~crc != entry.Crc32) throw new InvalidDataException($"ZIP integrity check failed: {name}");
        }
    }
    static class Crc32
    {
        static readonly uint[] Table = Enumerable.Range(0, 256).Select(i => { uint v = (uint)i; for (int b = 0; b < 8; b++) v = (v & 1) != 0 ? 0xEDB88320 ^ (v >> 1) : v >> 1; return v; }).ToArray();
        public static uint Update(uint crc, ReadOnlySpan<byte> data) { foreach (var b in data) crc = Table[(crc ^ b) & 255] ^ (crc >> 8); return crc; }
    }
}
