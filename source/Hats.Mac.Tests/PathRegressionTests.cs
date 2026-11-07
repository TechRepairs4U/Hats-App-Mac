using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Hats.Core;

public static class PathRegressionTests
{
    public static void Register(Action<string, Action> test, string[] args)
    {
        test("Filesystem root accepts valid child paths without writing to the root", () => {
            var root = Path.GetPathRoot(Path.GetFullPath(Environment.CurrentDirectory))!;
            var child = "hats-path-check-" + Guid.NewGuid().ToString("N") + "/file.bin";
            Check(Paths.Checked(root, child) == Path.GetFullPath(Path.Combine(root, child)), "Valid root child rejected");
        });
        test("Folder roots accept trailing separators and preserve containment", () => Temporary(temp => {
            foreach (var root in new[] { temp, temp + Path.DirectorySeparatorChar, temp.Replace('\\', '/') + "/" })
                Check(Paths.Checked(root, "nested/file.bin") == Path.Combine(temp, "nested", "file.bin"), "Trailing root separator failed");
        }));
        test("Drive root still rejects traversal, absolute paths and alternate streams", () => {
            var root = Path.GetPathRoot(Path.GetFullPath(Environment.CurrentDirectory))!;
            foreach (var path in new[] { "../escape", "folder/../../escape", "/absolute", "C:/escape", "file:stream", "", ".", "folder\\..\\escape" })
                Reject<InvalidDataException>(() => Paths.Checked(root, path));
        });
        if (!OperatingSystem.IsWindows()) return;
        test("Empty simulated drive root installs core files and recognised utilities", () => Scenario((source, drive, temp) => {
            Check(!Directory.EnumerateFileSystemEntries(drive).Any(), "Simulated card is not empty");
            var backup = Path.Combine(temp, "backup");
            var plan = new Installer().Install(source, drive, backup);
            VerifyCopies(source, drive, plan);
            Check(plan.Replace.Length == 0, "Empty card unexpectedly required replacement");
            Check(plan.Copy.Contains("switch/JKSV/JKSV.nro"), "Fresh utility was skipped");
            Check(File.Exists(Path.Combine(backup, "INSTALL-COMPLETE.txt")), "Install did not complete");
        }));
        test("Existing simulated drive updates, adds files, backs up and preserves user data", () => Scenario((source, drive, temp) => {
            new Installer().Install(source, drive, Path.Combine(temp, "first-backup"));
            var protectedFiles = AddUserData(drive);
            var before = protectedFiles.ToDictionary(p => p, p => (Hash(Path.Combine(drive, p)), File.GetLastWriteTimeUtc(Path.Combine(drive, p))));
            Put(drive, "bootloader/old-only.bin", "obsolete");
            Put(drive, "bootloader/ini/custom.ini", "user boot settings");
            Put(drive, "switch/JKSV/settings.ini", "user settings");
            Put(source, "atmosphere/package3", "updated core");
            Put(source, "new-root-file.bin", "new root file");
            Put(source, "switch/JKSV/settings.ini", "pack settings");
            var backup = Path.Combine(temp, "second-backup");
            var plan = new Installer().Install(source, drive, backup, ["custom/keep.dat"]);
            Check(File.ReadAllText(Path.Combine(drive, "atmosphere/package3")) == "updated core", "Existing core was not replaced");
            Check(File.ReadAllText(Path.Combine(backup, "atmosphere/package3")) == "original core", "Previous core was not backed up");
            Check(File.ReadAllText(Path.Combine(drive, "new-root-file.bin")) == "new root file", "New file was not installed");
            Check(!File.Exists(Path.Combine(drive, "bootloader/old-only.bin")), "Obsolete managed file remains");
            Check(File.Exists(Path.Combine(backup, "bootloader/old-only.bin")), "Obsolete file was not backed up");
            Check(File.ReadAllText(Path.Combine(drive, "bootloader/ini/custom.ini")) == "user boot settings", "Boot settings changed");
            Check(File.ReadAllText(Path.Combine(drive, "switch/JKSV/settings.ini")) == "user settings", "Utility settings changed");
            foreach (var (path, state) in before) {
                Check(Hash(Path.Combine(drive, path)) == state.Item1 && File.GetLastWriteTimeUtc(Path.Combine(drive, path)) == state.Item2, "Protected data changed: " + path);
                Check(!File.Exists(Path.Combine(backup, path)), "Protected data entered backup: " + path);
            }
            VerifyCopies(source, drive, plan);
            new Installer().Install(source, drive, Path.Combine(temp, "third-backup"), ["custom/keep.dat"]);
            Check(File.ReadAllText(Path.Combine(drive, "atmosphere/package3")) == "updated core", "Repeat update failed");
        }));
        test("Write failure at a simulated drive root restores previous files", () => Scenario((source, drive, temp) => {
            new Installer().Install(source, drive, Path.Combine(temp, "first-backup"));
            AddUserData(drive);
            var before = Snapshot(drive);
            Put(source, "atmosphere/package3", "updated core");
            Put(source, "new-file.bin", "new");
            var copied = 0;
            var installer = new Installer { CopyFile = (from, to) => { if (++copied == 2) throw new IOException("Simulated write failure"); VerifiedFiles.Copy(from, to); } };
            Reject<IOException>(() => installer.Install(source, drive, Path.Combine(temp, "failed-backup")));
            var after = Snapshot(drive);
            Check(before.Count == after.Count && before.All(p => after.TryGetValue(p.Key, out var hash) && hash == p.Value), "Rollback did not restore the card");
        }));
        var packIndex = Array.IndexOf(args, "--real-pack");
        if (packIndex >= 0 && packIndex + 1 < args.Length)
            test("Real pack installs on empty drive root and updates it with protections", () => Temporary(temp => {
                var extracted = Path.Combine(temp, "extracted");
                ArchiveService.Extract(Path.GetFullPath(args[packIndex + 1]), extracted);
                var source = InstallPolicy.FindPack(extracted);
                ExclusionSnapshot? exclusions = null;
                if (args.Contains("--live-exclusions")) { using var service = new ExclusionService(); exclusions = service.LatestAsync().GetAwaiter().GetResult(); }
                WithDrive(Path.Combine(temp, "card"), drive => {
                    var first = new Installer().Install(source, drive, Path.Combine(temp, "first-backup"), exclusions: exclusions);
                    VerifyCopies(source, drive, first);
                    Check(first.Copy.Length > 100 && first.Replace.Length == 0, "Real fresh install incomplete");
                    var protectedFiles = AddUserData(drive);
                    var before = protectedFiles.ToDictionary(p => p, p => Hash(Path.Combine(drive, p)));
                    Put(drive, "atmosphere/package3", "old core");
                    Put(drive, "bootloader/obsolete-test-file.bin", "old");
                    var backup = Path.Combine(temp, "update-backup");
                    var update = new Installer().Install(source, drive, backup, ["custom/keep.dat"], exclusions: exclusions);
                    VerifyCopies(source, drive, update);
                    Check(File.ReadAllText(Path.Combine(backup, "atmosphere/package3")) == "old core", "Real update backup failed");
                    Check(!File.Exists(Path.Combine(drive, "bootloader/obsolete-test-file.bin")), "Real update cleanup failed");
                    foreach (var (path, hash) in before) {
                        Check(Hash(Path.Combine(drive, path)) == hash, "Real update changed protected data: " + path);
                        Check(!File.Exists(Path.Combine(backup, path)), "Real update backed up protected data: " + path);
                    }
                    Console.WriteLine($"  Drive-root real pack: fresh {first.Copy.Length} files; update {update.Copy.Length} files; exclusions {exclusions?.PreservePaths.Length ?? 0}.");
                });
            }));
    }

    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Reject<T>(Action body) where T : Exception { try { body(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    static void Put(string root, string relative, string text) { var path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
    static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    static Dictionary<string, string> Snapshot(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => Paths.Relative(root, p), Hash);
    static void VerifyCopies(string source, string drive, InstallPlan plan) { foreach (var path in plan.Copy) Check(Hash(Path.Combine(source, path)) == Hash(Path.Combine(drive, path)), "Copy mismatch: " + path); }
    static string[] AddUserData(string drive) {
        string[] paths = ["atmosphere/contents/0100B00B51230000/romfs/game.dat", "atmosphere/contents/0100111122223333/romfs/mod.dat", "switch/my-game/save.dat", "Nintendo/user-data.bin", "custom/keep.dat"];
        foreach (var path in paths) Put(drive, path, "user content");
        return paths;
    }
    static void Temporary(Action<string> body) {
        var temp = Path.Combine(Path.GetTempPath(), "hats-root-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try { body(temp); } finally { Directory.Delete(temp, true); }
    }
    static void Scenario(Action<string, string, string> body) => Temporary(temp => {
        var source = Path.Combine(temp, "source");
        Put(source, "atmosphere/package3", "original core"); Put(source, "bootloader/update.bin", "bootloader"); Put(source, "hbmenu.nro", "root menu");
        var bytes = new byte[0x80 + 0x38 + 0x3000];
        "NRO0"u8.CopyTo(bytes.AsSpan(0x10)); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x18), 0x80);
        "ASET"u8.CopyTo(bytes.AsSpan(0x80)); BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x98), 0x38); BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0xa0), 0x3000);
        Encoding.UTF8.GetBytes("JKSV").CopyTo(bytes, 0xb8); Encoding.UTF8.GetBytes("JK").CopyTo(bytes, 0x2b8);
        Directory.CreateDirectory(Path.Combine(source, "switch", "JKSV")); File.WriteAllBytes(Path.Combine(source, "switch", "JKSV", "JKSV.nro"), bytes);
        WithDrive(Path.Combine(temp, "card"), drive => body(source, drive, temp));
    });
    // SUBST maps only this freshly created test folder. No physical card is selected.
    static void WithDrive(string backing, Action<string> body) {
        Directory.CreateDirectory(backing);
        var existing = Directory.GetLogicalDrives().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var letter = Enumerable.Range('R', 'Z' - 'R' + 1).Reverse().Select(c => ((char)c) + ":").First(d => !existing.Contains(d + "\\"));
        Subst(letter, backing);
        try {
            var marker = "hats-test-mapping-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(Path.Combine(backing, marker), marker);
            Check(File.ReadAllText(Path.Combine(letter + "\\", marker)) == marker, "Virtual drive mapping does not match the test folder");
            File.Delete(Path.Combine(backing, marker));
            body(letter + "\\");
        } finally { Subst(letter, "/D"); }
    }
    static void Subst(string drive, string argument) {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "subst.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(drive); start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
        Check(process.ExitCode == 0, "SUBST failed: " + output + error);
    }
}
