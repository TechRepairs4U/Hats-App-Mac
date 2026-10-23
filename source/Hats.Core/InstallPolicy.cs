using System.Text.Json;

namespace Hats.Core;

public sealed record InstallPlan(string[] Replace, string[] Copy, string[] Directories, string[] Preserved, string[] Sysmodules, Dictionary<string, string> AutomaticallyPreserved);

public static class InstallPolicy
{
    public static string[] LoadPreserved(string config)
    {
        var paths = new HashSet<string>(Catalog.Current.Defaults, StringComparer.OrdinalIgnoreCase);
        if (File.Exists(config))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(config));
            if (!json.RootElement.TryGetProperty("preserve_paths", out var list) || list.ValueKind != JsonValueKind.Array) throw new InvalidDataException("The protection file must contain a preserve_paths list.");
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) throw new InvalidDataException("Protected paths must be text.");
                var path = item.GetString()!; Paths.ValidateRelative(path); paths.Add(Paths.Key(path));
            }
        }
        return paths.Order().ToArray();
    }
    public static bool IsPack(string root) => File.Exists(Path.Combine(root, "atmosphere", "package3")) && Directory.Exists(Path.Combine(root, "switch")) && Directory.Exists(Path.Combine(root, "bootloader"));
    public static string FindPack(string root)
    {
        var matches = new[] { root }.Concat(Directory.EnumerateDirectories(root)).Where(IsPack).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidDataException("The ZIP must contain one HATS pack with atmosphere/package3, bootloader and switch.");
    }
    public static InstallPlan Plan(string source, string sd, IEnumerable<string>? preserve = null)
    {
        source = Paths.Root(source); sd = Paths.Root(sd);
        if (Paths.Overlap(source, sd)) throw new IOException("The extracted pack and SD card must be separate folders.");
        if (!Directory.Exists(sd)) throw new IOException("The SD card is no longer connected.");
        if (!IsPack(source)) throw new InvalidDataException("Incomplete HATS pack: atmosphere/package3, bootloader and switch are required.");
        var protectedPaths = new HashSet<string>(Catalog.Current.Defaults.Select(Paths.Key));
        foreach (var path in preserve ?? []) { Paths.ValidateRelative(path); protectedPaths.Add(Paths.Key(path)); }
        bool Excluded(string path) => protectedPaths.Any(p => Paths.Under(Paths.Key(path), p));
        var modules = new Dictionary<string, string>();
        var packContents = Paths.Checked(source, "atmosphere/contents");
        if (Directory.Exists(packContents) && !Excluded("atmosphere/contents"))
            foreach (var folder in Paths.Children(packContents))
            {
                var relative = Paths.Relative(source, folder); var id = Path.GetFileName(folder).ToLowerInvariant();
                if (Excluded(relative)) continue;
                if (!Catalog.Current.Modules.ContainsKey(id) || !File.Exists(Paths.Checked(source, relative + "/exefs.nsp"))) { protectedPaths.Add(Paths.Key(relative)); continue; }
                modules[id] = relative;
                foreach (var name in new[] { "romfs", "romfs.bin", "exefs", "cheats" }) protectedPaths.Add(Paths.Key(relative + "/" + name));
            }
        var contents = Paths.Checked(sd, "atmosphere/contents");
        if (Directory.Exists(contents))
            foreach (var item in Paths.Children(contents))
                if (!modules.ContainsKey(Path.GetFileName(item).ToLowerInvariant())) protectedPaths.Add(Paths.Key(Paths.Relative(sd, item)));

        var incoming = new List<string>(); var directories = new List<string>();
        void Scan(string folder)
        {
            foreach (var item in Paths.Children(folder))
            {
                var relative = Paths.Relative(source, item);
                if (Excluded(relative)) continue;
                Paths.Checked(source, relative);
                if (Directory.Exists(item)) { directories.Add(relative); Scan(item); } else incoming.Add(relative);
            }
        }
        Scan(source);
        var automatic = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var candidates = incoming.Where(p => p.StartsWith("switch/", StringComparison.OrdinalIgnoreCase) && (p.EndsWith(".nro", StringComparison.OrdinalIgnoreCase) || p.StartsWith("switch/.overlays/", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".ovl", StringComparison.OrdinalIgnoreCase)));
        static string Owner(string path)
        {
            var parts = path.Split('/');
            if (parts.Length == 2 || parts[1].Equals(".overlays", StringComparison.OrdinalIgnoreCase)) return parts.Length <= 3 ? path : path[..path.LastIndexOf('/')];
            return parts[0] + "/" + parts[1];
        }
        void Protect(string path, string reason) { protectedPaths.Add(Paths.Key(path)); automatic.TryAdd(path, reason); }
        var identities = new Dictionary<string, AppIdentity>(); var existingOwners = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in candidates)
        {
            var group = Owner(relative);
            if (Excluded(relative)) continue;
            if (Catalog.Current.GameNames.Contains(AppIdentifier.Normalize(Path.GetFileNameWithoutExtension(relative)))) { Protect(group, "game, port or emulator name"); continue; }
            var reason = group != relative ? AppIdentifier.GameFolderReason(Path.Combine(source, group)) : null;
            if (reason != null) { Protect(group, reason); continue; }
            var identity = AppIdentifier.Read(Path.Combine(source, relative));
            if (identity == null) { Protect(group, "pack executable is not a recognised HATS utility"); continue; }
            string target, groupTarget;
            try { target = Paths.Checked(sd, relative); groupTarget = Paths.Checked(sd, group); }
            catch (IOException) { Protect(group, "linked or conflicting existing path"); continue; }
            if (group != relative && Paths.Exists(groupTarget))
            {
                existingOwners.TryAdd(group, false);
                reason = AppIdentifier.GameFolderReason(groupTarget);
                if (reason != null) { Protect(group, reason); continue; }
            }
            if (Paths.Exists(target))
            {
                var previous = AppIdentifier.Read(target);
                if (previous == null || previous.Utility != identity.Utility) { Protect(group, "existing executable is a game, port or unrecognised/different app"); continue; }
                if (existingOwners.ContainsKey(group)) existingOwners[group] = true;
            }
            identities[relative] = identity;
        }
        foreach (var (group, identified) in existingOwners)
        {
            if (Excluded(group)) continue;
            if (!identified) { Protect(group, "existing folder has no matching recognised utility"); continue; }
            try
            {
                if (Paths.Children(Path.Combine(sd, group)).Any(item => item.EndsWith(".nro", StringComparison.OrdinalIgnoreCase) && AppIdentifier.Read(item) == null)) Protect(group, "game or unrecognised launcher shares this folder");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Protect(group, "existing folder could not be identified safely"); }
        }
        var managedApps = identities.Keys.Where(p => !Excluded(p)).Select(Paths.Key).ToHashSet();
        var managedDirs = managedApps.Select(p => p.Split('/')).Where(p => p.Length > 2).Select(p => p[1]).ToHashSet();
        foreach (var relative in directories)
        {
            var parts = relative.Split('/');
            if (parts.Length == 2 && parts[0].Equals("switch", StringComparison.OrdinalIgnoreCase) && !managedDirs.Contains(parts[1].ToLowerInvariant()) && !Excluded(relative))
            {
                var reason = AppIdentifier.GameFolderReason(Path.Combine(source, relative));
                if (reason != null) Protect(relative, reason);
            }
        }
        var switchFolder = Paths.Checked(sd, "switch");
        if (Directory.Exists(switchFolder))
            foreach (var item in Paths.Children(switchFolder))
                if (Directory.Exists(item) && !managedDirs.Contains(Path.GetFileName(item).ToLowerInvariant()))
                {
                    var relative = Paths.Relative(sd, item);
                    if (!Excluded(relative)) Protect(relative, "unrelated app, game or port folder");
                }

        var replace = new List<string>();
        void SelectReplacement(string relative)
        {
            if (Excluded(relative)) return;
            var target = Paths.Checked(sd, relative);
            if (!Paths.Exists(target)) return;
            if (protectedPaths.Any(p => Paths.Under(p, Paths.Key(relative))))
            {
                if (!Directory.Exists(target)) throw new IOException($"Protected folder conflicts with a file: {relative}");
                foreach (var child in Paths.Children(target)) SelectReplacement(Paths.Relative(sd, child));
            }
            else replace.Add(relative);
        }
        foreach (var folder in new[] { "atmosphere", "bootloader" })
        {
            var target = Paths.Checked(sd, folder);
            if (!Paths.Exists(target)) continue;
            if (!Directory.Exists(target)) throw new IOException($"Expected an SD card directory: {folder}");
            if (folder == "atmosphere") { foreach (var child in Paths.Children(target)) if (!Path.GetFileName(child).Equals("contents", StringComparison.OrdinalIgnoreCase)) SelectReplacement(Paths.Relative(sd, child)); }
            else SelectReplacement(folder);
        }
        foreach (var relative in modules.Values) SelectReplacement(relative);
        var copies = new List<string>();
        foreach (var relative in incoming)
        {
            if (Excluded(relative)) continue;
            var target = Paths.Checked(sd, relative);
            if (relative.StartsWith("switch/", StringComparison.OrdinalIgnoreCase) && Paths.Exists(target))
            {
                if (!managedApps.Contains(Paths.Key(relative))) continue;
                if (!File.Exists(target)) throw new IOException($"App file conflicts with an SD directory: {relative}");
                replace.Add(relative);
            }
            else if (Paths.Exists(target) && !File.Exists(target)) throw new IOException($"Pack file conflicts with an SD directory: {relative}");
            if (Paths.Exists(target) && !replace.Any(p => Paths.Under(Paths.Key(relative), Paths.Key(p)))) replace.Add(relative);
            copies.Add(relative);
        }
        var outputDirs = directories.Where(p => !Excluded(p)).ToArray();
        foreach (var relative in outputDirs)
        {
            var target = Paths.Checked(sd, relative);
            if (Paths.Exists(target) && !Directory.Exists(target)) throw new IOException($"Pack directory conflicts with an SD file: {relative}");
        }
        return new(replace.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), copies.ToArray(), outputDirs, protectedPaths.Order().ToArray(), modules.Keys.Order().ToArray(), automatic);
    }
}
