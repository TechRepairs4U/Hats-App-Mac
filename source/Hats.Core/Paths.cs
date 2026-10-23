namespace Hats.Core;

public static class Paths
{
    public static string Key(string path) => path.Replace('\\', '/').Trim('/').ToLowerInvariant();
    public static bool Under(string path, string parent) => path.Equals(parent, StringComparison.OrdinalIgnoreCase) || path.StartsWith(parent.TrimEnd('/', '\\') + "/", StringComparison.OrdinalIgnoreCase);
    public static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    public static bool Linked(string path) { try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; } catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; } }
    public static string Root(string path)
    {
        var full = Path.GetFullPath(path);
        for (var p = full; p != null; p = Directory.GetParent(p)?.FullName)
            if (Linked(p)) throw new IOException($"Linked folders cannot be used: {p}");
        return Path.TrimEndingDirectorySeparator(full);
    }
    public static void ValidateRelative(string relative)
    {
        var value = relative.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('/') || value.Split('/').Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.Any(c => c < 32 || "<>:\"|?*".Contains(c)) || IsReserved(p)))
            throw new InvalidDataException($"Unsafe relative path: {relative}");
    }
    static bool IsReserved(string name)
    {
        var stem = name.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3]));
    }
    public static string Checked(string root, string relative)
    {
        ValidateRelative(relative);
        root = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("A path leaves the selected folder.");
        for (var p = path; !p.Equals(root, StringComparison.OrdinalIgnoreCase); p = Path.GetDirectoryName(p)!)
            if (Linked(p)) throw new IOException($"Linked paths cannot be updated: {relative}");
        if (Linked(root)) throw new IOException("The selected root is a linked folder.");
        return path;
    }
    public static bool Overlap(string a, string b)
    {
        a = Key(Path.GetFullPath(a)); b = Key(Path.GetFullPath(b));
        return Under(a, b) || Under(b, a);
    }
    public static IEnumerable<string> Children(string folder) => Directory.EnumerateFileSystemEntries(folder).Order(StringComparer.OrdinalIgnoreCase);
    public static string MountForPath(string path, IEnumerable<string> mounts)
    {
        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return mounts.Select(Path.GetFullPath).OrderByDescending(p => p.Length).FirstOrDefault(p =>
            full.Equals(p, comparison) || full.StartsWith(Path.TrimEndingDirectorySeparator(p) + Path.DirectorySeparatorChar, comparison) || p == Path.GetPathRoot(full))
            ?? throw new IOException("Could not identify the disk containing this folder.");
    }
    public static long FreeSpace(string path) => new DriveInfo(MountForPath(path, DriveInfo.GetDrives().Select(d => d.Name))).AvailableFreeSpace;
}
