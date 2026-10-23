using System.Security.Cryptography;
using System.Text.Json;

namespace Hats.Core;

public sealed record JobProgress(string Phase, double Percent, bool CanCancel);

public static class VerifiedFiles
{
    public static void Copy(string source, string target)
    {
        if (Paths.Linked(source) || Paths.Linked(target)) throw new IOException("Linked files cannot be copied.");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, ".hats-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            byte[] digest;
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1048576, FileOptions.WriteThrough))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[1048576]; int n;
                while ((n = input.Read(buffer)) > 0) { hash.AppendData(buffer, 0, n); output.Write(buffer, 0, n); }
                output.Flush(true); digest = hash.GetHashAndReset();
            }
            using (var verify = File.OpenRead(temporary)) if (!SHA256.HashData(verify).SequenceEqual(digest)) throw new IOException($"Copy verification failed: {Path.GetFileName(source)}");
            File.SetLastWriteTimeUtc(temporary, File.GetLastWriteTimeUtc(source));
            if (File.Exists(target)) ClearAttributes(target);
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static void ClearAttributes(string path) => File.SetAttributes(path, File.GetAttributes(path) & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));
    public static void CopyTree(string sourceRoot, string source, string targetRoot, string target)
    {
        Paths.Checked(sourceRoot, Paths.Relative(sourceRoot, source)); Paths.Checked(targetRoot, Paths.Relative(targetRoot, target));
        if (!Directory.Exists(source)) { Copy(source, target); return; }
        Directory.CreateDirectory(target);
        foreach (var item in Paths.Children(source)) CopyTree(sourceRoot, item, targetRoot, Path.Combine(target, Path.GetFileName(item)));
    }
    public static void Remove(string root, string relative)
    {
        var path = Paths.Checked(root, relative);
        if (Directory.Exists(path))
        {
            foreach (var child in Paths.Children(path)) Remove(root, Paths.Relative(root, child));
            ClearAttributes(path); Directory.Delete(path);
        }
        else if (File.Exists(path)) { ClearAttributes(path); File.Delete(path); }
    }
    public static long TreeSize(string root, string relative)
    {
        var path = Paths.Checked(root, relative);
        return Directory.Exists(path) ? Paths.Children(path).Sum(child => TreeSize(root, Paths.Relative(root, child))) : (new FileInfo(path).Length + 4095) / 4096 * 4096;
    }
}

public sealed class Installer
{
    // A test seam for simulated write failures; production uses verified copies.
    public Action<string, string> CopyFile { get; init; } = VerifiedFiles.Copy;
    public Func<ExclusionService> CreateExclusionService { get; init; } = () => new();
    public long MaximumTargetFileBytes { get; init; } = long.MaxValue;
    public string WorkFolder { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "MagicMonkeiHATS", "Work");
    public InstallPlan Install(string source, string sd, string backup, IEnumerable<string>? preserve = null, Action<string>? log = null, Action<JobProgress>? progress = null, CancellationToken cancel = default, Action? validateTarget = null, ExclusionSnapshot? exclusions = null)
    {
        source = Paths.Root(source); sd = Paths.Root(sd); backup = Paths.Root(backup);
        if (Paths.Overlap(sd, backup) || Paths.Overlap(source, backup)) throw new IOException("The backup folder must be separate from the SD card and extracted pack.");
        if (Paths.Exists(backup)) throw new IOException("The backup folder already exists. Use a new backup folder.");
        var plan = InstallPolicy.Plan(source, sd, (preserve ?? []).Concat(exclusions?.PreservePaths ?? [])); validateTarget?.Invoke();
        var backupSize = plan.Replace.Sum(p => VerifiedFiles.TreeSize(sd, p));
        var sizes = plan.Copy.Select(p => new FileInfo(Paths.Checked(source, p)).Length).ToArray();
        if (sizes.Any(size => size > MaximumTargetFileBytes)) throw new IOException("A pack file is too large for this SD card's file system. No backup or SD changes were started.");
        long largest = sizes.DefaultIfEmpty(0).Max(), reserve = 16L * 1048576;
        if (Paths.FreeSpace(backup) < backupSize + largest + reserve) throw new IOException("Not enough free space in the backup location.");
        if (Paths.FreeSpace(sd) < sizes.Sum(n => (n + 32767) / 32768 * 32768) + largest + reserve) throw new IOException("Not enough free space on the SD card to install safely.");
        cancel.ThrowIfCancellationRequested();
        log?.Invoke($"Updating {plan.Sysmodules.Length} identified HATS sysmodules. Games, mods, ports and console backups are protected.");
        foreach (var (path, reason) in plan.AutomaticallyPreserved.Take(20)) log?.Invoke($"Skipping {path}: {reason}.");
        if (plan.AutomaticallyPreserved.Count > 20) log?.Invoke($"{plan.AutomaticallyPreserved.Count - 20} more unrelated paths are protected.");
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "HATS-recovery-plan.json"), JsonSerializer.Serialize(new { Target = sd, CreatedUtc = DateTime.UtcNow, Plan = plan, GitHubExclusions = exclusions }, new JsonSerializerOptions { WriteIndented = true }));
        if (exclusions != null) File.WriteAllText(Path.Combine(backup, "HATS-exclusions-used.json"), exclusions.Json);
        for (int i = 0; i < plan.Replace.Length; i++)
        {
            cancel.ThrowIfCancellationRequested(); validateTarget?.Invoke();
            var path = plan.Replace[i]; log?.Invoke($"Backing up {path}");
            VerifiedFiles.CopyTree(sd, Paths.Checked(sd, path), backup, Paths.Checked(backup, path));
            progress?.Invoke(new("Backing up", (i + 1) * 35d / Math.Max(1, plan.Replace.Length), true));
        }
        log?.Invoke($"Verified backup: {backup}");
        cancel.ThrowIfCancellationRequested(); validateTarget?.Invoke();
        progress?.Invoke(new("Installing", 35, false));
        var newFiles = plan.Copy.Where(p => !Paths.Exists(Paths.Checked(sd, p))).ToArray();
        var newDirs = plan.Directories.Where(p => !Paths.Exists(Paths.Checked(sd, p))).ToArray();
        var changed = new List<string>();
        try
        {
            foreach (var relative in plan.Replace) { validateTarget?.Invoke(); changed.Add(relative); VerifiedFiles.Remove(sd, relative); }
            foreach (var relative in plan.Directories) { validateTarget?.Invoke(); Directory.CreateDirectory(Paths.Checked(sd, relative)); }
            for (int i = 0; i < plan.Copy.Length; i++)
            {
                validateTarget?.Invoke(); var relative = plan.Copy[i]; CopyFile(Paths.Checked(source, relative), Paths.Checked(sd, relative));
                progress?.Invoke(new("Installing", 35 + (i + 1) * 60d / Math.Max(1, plan.Copy.Length), false));
            }
            var savedIni = Paths.Checked(backup, "bootloader/ini");
            if (Directory.Exists(savedIni)) { validateTarget?.Invoke(); log?.Invoke("Restoring your bootloader settings."); VerifiedFiles.CopyTree(backup, savedIni, sd, Paths.Checked(sd, "bootloader/ini")); }
            File.WriteAllText(Path.Combine(backup, "INSTALL-COMPLETE.txt"), "HATS installation completed at " + DateTime.UtcNow.ToString("O"));
        }
        catch (Exception failure)
        {
            progress?.Invoke(new("Restoring backup", 0, false)); log?.Invoke("Update failed. Restoring files from the verified backup…");
            try
            {
                foreach (var relative in newFiles) { validateTarget?.Invoke(); VerifiedFiles.Remove(sd, relative); }
                foreach (var relative in changed)
                {
                    validateTarget?.Invoke(); VerifiedFiles.Remove(sd, relative);
                    VerifiedFiles.CopyTree(backup, Paths.Checked(backup, relative), sd, Paths.Checked(sd, relative));
                }
                foreach (var relative in newDirs.OrderByDescending(p => p.Count(c => c == '/')))
                {
                    validateTarget?.Invoke(); var path = Paths.Checked(sd, relative);
                    if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
                }
            }
            catch (Exception recovery) { throw new IOException($"Update failed: {failure.Message}\nAutomatic restoration could not finish: {recovery.Message}\nKeep the SD card and restore files from: {backup}", failure); }
            throw new IOException($"Update failed: {failure.Message}\nYour previous files were restored.\nBackup: {backup}", failure);
        }
        log?.Invoke("HATS updated. Protected games, mods and ports were left in place."); progress?.Invoke(new("Complete", 100, false));
        return plan;
    }

    public async Task<InstallPlan> InstallWithLatestExclusionsAsync(string source, string sd, string backup, IEnumerable<string>? preserve = null, Action<string>? log = null, Action<JobProgress>? progress = null, CancellationToken cancel = default, Action? validateTarget = null)
    {
        cancel.ThrowIfCancellationRequested(); validateTarget?.Invoke();
        progress?.Invoke(new("Refreshing GitHub exclusions", 0, true));
        using var service = CreateExclusionService();
        var latest = await service.LatestAsync(cancel);
        log?.Invoke($"GitHub exclusions: {latest.Repository}/{latest.File}, {latest.PreservePaths.Length} paths, blob {latest.BlobSha}, checked {latest.FetchedUtc:O}.");
        foreach (var path in latest.PreservePaths) log?.Invoke("GitHub protection: " + path);
        cancel.ThrowIfCancellationRequested(); validateTarget?.Invoke();
        return Install(source, sd, backup, preserve, log, progress, cancel, validateTarget, latest);
    }

    public async Task<InstallPlan> RunAsync(HatsRelease selectedRelease, PackAsset asset, string sd, string backup, IEnumerable<string>? preserve, Action<string> log, Action<JobProgress> progress, CancellationToken cancel, Action validateTarget)
    {
        using var releases = new ReleaseService();
        progress(new("Checking release", 0, true)); ReleaseService.Confirm(selectedRelease, asset, await releases.LatestAsync(cancel)); validateTarget();
        var temporary = Path.Combine(Paths.Root(WorkFolder), "HATS-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temporary);
        try
        {
            var zip = Path.Combine(temporary, "pack.zip"); log($"Downloading {asset.Name} from the latest {ReleaseService.Repository} release.");
            await releases.DownloadAsync(asset, zip, p => progress(new("Downloading", p * .3, true)), cancel);
            var unpacked = Path.Combine(temporary, "pack");
            ArchiveService.Extract(zip, unpacked, p => progress(new("Checking pack", 30 + p * .1, true)), cancel);
            var source = InstallPolicy.FindPack(unpacked); cancel.ThrowIfCancellationRequested();
            return await InstallWithLatestExclusionsAsync(source, sd, backup, preserve, log, p => progress(p with { Percent = 40 + p.Percent * .6 }), cancel, validateTarget);
        }
        finally { log($"Download and working files retained: {temporary}"); }
    }
}
