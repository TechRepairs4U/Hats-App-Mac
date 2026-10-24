using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hats.Core;


namespace Hats.Mac.Tests;
public static class Regression { public static async Task<int> Run(string[] args) {
if (args.Contains("--check-exclusions"))
{
    using var service = new ExclusionService(); var snapshot = await service.LatestAsync();
    Console.WriteLine(JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

var tests = new List<(string Name, Action Body)>();
void Test(string name, Action body) => tests.Add((name, body));
PathRegressionTests.Register(Test, args);
AppUpdateTests.Register(Test, args);
MacUpdateTests.Register(Test);
void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Throws(Action body) { try { body(); } catch { return; } throw new Exception("Expected a failure"); }
void Write(string root, string path, string text = "data") { var p = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
void Nro(string root, string path, string title = "JKSV", string author = "JK", string tail = "")
{
    var bytes = new byte[0x80 + 0x38 + 0x3000]; "NRO0"u8.CopyTo(bytes.AsSpan(0x10)); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x18), 0x80); "ASET"u8.CopyTo(bytes.AsSpan(0x80)); BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x98), 0x38); BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0xa0), 0x3000); Encoding.UTF8.GetBytes(title).CopyTo(bytes, 0xb8); Encoding.UTF8.GetBytes(author).CopyTo(bytes, 0x2b8);
    var p = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllBytes(p, bytes.Concat(Encoding.UTF8.GetBytes(tail)).ToArray());
}
string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
Dictionary<string, string> Snapshot(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => Paths.Relative(root, p), Hash);
void EqualSnapshot(Dictionary<string, string> expected, string root) { var actual = Snapshot(root); Assert(expected.Count == actual.Count && expected.All(kv => actual.TryGetValue(kv.Key, out var hash) && hash == kv.Value), "Filesystem snapshot changed"); }
string Scratch() { var p = Path.Combine(Environment.GetEnvironmentVariable("HATS_TEST_ROOT") ?? Path.Combine(AppContext.BaseDirectory, "fixtures"), "hats-csharp-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }
void Scenario(Action<string, string, string> body)
{
    var temp = Scratch(); try { var source = Path.Combine(temp, "source"); var sd = Path.Combine(temp, "sd"); Directory.CreateDirectory(sd); Write(source, "atmosphere/package3", "new"); Write(source, "bootloader/update.bin", "new"); Directory.CreateDirectory(Path.Combine(source, "switch")); body(source, sd, Path.Combine(temp, "backup")); } finally { GC.KeepAlive(temp); }
}
const string Module = "atmosphere/contents/010000000000bd00";
const string Gta = "atmosphere/contents/0100B00B51230000";

Test("GTA, unknown mods and console backups never enter backup/copy/delete", () => Scenario((s, d, b) => {
    foreach (var p in new[] { Gta + "/romfs/game.dat", "atmosphere/contents/0100111122223333/exefs.nsp", "atmosphere/automatic_backups/BIS.keys" }) { Write(s, p, "pack"); Write(d, p, "user"); }
    var before = Snapshot(d); var plan = new Installer().Install(s, d, b);
    foreach (var (p, hash) in before) { Assert(Hash(Path.Combine(d, p)) == hash); Assert(!File.Exists(Path.Combine(b, p))); Assert(!plan.Copy.Contains(p)); }
}));
Test("Known sysmodule updates while its mod data stays in place", () => Scenario((s, d, b) => {
    Write(s, Module + "/exefs.nsp", "new module"); Write(d, Module + "/exefs.nsp", "old module"); Write(d, Module + "/old-file", "old");
    foreach (var name in new[] { "romfs/game.dat", "romfs.bin", "exefs/main", "cheats/cheat.txt" }) { Write(s, Module + "/" + name, "pack"); Write(d, Module + "/" + name, "user"); }
    var plan = new Installer().Install(s, d, b); Assert(plan.Sysmodules.Length == 1); Assert(File.ReadAllText(Path.Combine(d, Module, "exefs.nsp")) == "new module"); Assert(!File.Exists(Path.Combine(d, Module, "old-file")));
    foreach (var name in new[] { "romfs/game.dat", "romfs.bin", "exefs/main", "cheats/cheat.txt" }) { Assert(File.ReadAllText(Path.Combine(d, Module, name)) == "user"); Assert(!File.Exists(Path.Combine(b, Module, name))); }
}));
Test("Known module absent from pack stays untouched", () => Scenario((s, d, b) => { Write(d, Module + "/exefs.nsp"); var p = InstallPolicy.Plan(s, d); Assert(!p.Replace.Any(x => x.StartsWith(Module))); }));
Test("Unreviewed incoming module is not treated as core", () => Scenario((s, d, b) => { Write(s, "atmosphere/contents/0100123456789000/exefs.nsp"); Assert(!InstallPolicy.Plan(s, d).Copy.Any(p => p.Contains("0100123456789000"))); }));
Test("Recognised matching switch utility updates and keeps app data", () => Scenario((s, d, b) => {
    Nro(s, "switch/JKSV/JKSV.nro", tail: "new"); Nro(d, "switch/JKSV/JKSV.nro", tail: "old"); Write(s, "switch/JKSV/settings.ini", "pack"); Write(d, "switch/JKSV/settings.ini", "user"); Write(d, "switch/JKSV/saves/save", "save");
    new Installer().Install(s, d, b); Assert(Hash(Path.Combine(s, "switch/JKSV/JKSV.nro")) == Hash(Path.Combine(d, "switch/JKSV/JKSV.nro"))); Assert(File.ReadAllText(Path.Combine(d, "switch/JKSV/settings.ini")) == "user"); Assert(!Directory.Exists(Path.Combine(b, "switch/JKSV/saves")));
}));
Test("Existing unknown app with same filename stays protected", () => Scenario((s, d, b) => { Nro(s, "switch/ordinary/JKSV.nro"); Nro(d, "switch/ordinary/JKSV.nro", "My Game", "Port Author"); Write(s, "switch/ordinary/data.bin", "pack"); Write(d, "switch/ordinary/data.bin", "game"); var before = Snapshot(d); var p = new Installer().Install(s, d, b); foreach (var kv in before) Assert(Hash(Path.Combine(d, kv.Key)) == kv.Value); Assert(!p.Copy.Any(x => x.StartsWith("switch/ordinary"))); }));
Test("Pack with unrecognised executable is skipped", () => Scenario((s, d, b) => { Nro(s, "switch/not-obviously-a-game/start.nro", "Unknown Adventure", "Developer"); Write(s, "switch/not-obviously-a-game/data.pak"); var p = InstallPolicy.Plan(s, d); Assert(!p.Copy.Any(x => x.StartsWith("switch/not-obviously"))); }));
Test("Ordinary existing folder without a matching app stays opaque", () => Scenario((s, d, b) => { Nro(s, "switch/ordinary/JKSV.nro"); Write(d, "switch/ordinary/game-data/data", "user"); Assert(!InstallPolicy.Plan(s, d).Copy.Any(x => x.StartsWith("switch/ordinary"))); }));
Test("Game marker overrides a recognised utility in an ordinary folder", () => Scenario((s, d, b) => { Nro(s, "switch/plain/helper.nro"); Nro(d, "switch/plain/helper.nro"); Write(d, "switch/plain/doom.wad"); Assert(!InstallPolicy.Plan(s, d).Copy.Any(x => x.StartsWith("switch/plain"))); }));
Test("Unknown launcher sharing a utility folder protects the entire folder", () => Scenario((s, d, b) => { Nro(s, "switch/plain/helper.nro"); Nro(d, "switch/plain/helper.nro"); Nro(d, "switch/plain/play.nro", "A Game", "Game Author"); Assert(!InstallPolicy.Plan(s, d).Copy.Any(x => x.StartsWith("switch/plain"))); }));
Test("Known game folder overrides metadata", () => Scenario((s, d, b) => { Nro(s, "switch/GTA V/helper.nro"); Assert(!InstallPolicy.Plan(s, d).Copy.Any(x => x.StartsWith("switch/GTA"))); }));
Test("Unknown overlay does not block identified overlays", () => Scenario((s, d, b) => { Nro(s, "switch/.overlays/JKSV.ovl"); Nro(s, "switch/.overlays/game.ovl", "Game", "Developer"); var p = InstallPolicy.Plan(s, d); Assert(p.Copy.Contains("switch/.overlays/JKSV.ovl") && !p.Copy.Contains("switch/.overlays/game.ovl")); }));
Test("Root utilities and unrelated root NROs coexist", () => Scenario((s, d, b) => { Nro(s, "switch/JKSV.nro", tail: "new"); Nro(d, "switch/JKSV.nro", tail: "old"); Nro(d, "switch/game.nro", "Game", "Porter"); var old = Hash(Path.Combine(d, "switch/game.nro")); new Installer().Install(s, d, b); Assert(Hash(Path.Combine(d, "switch/game.nro")) == old); }));
Test("Extra protected path splits a core replacement", () => Scenario((s, d, b) => { Write(s, "atmosphere/config/user.ini", "pack"); Write(s, "atmosphere/config/core.ini", "new"); Write(d, "atmosphere/config/user.ini", "user"); Write(d, "atmosphere/config/obsolete.ini"); new Installer().Install(s, d, b, ["atmosphere/config/user.ini"]); Assert(File.ReadAllText(Path.Combine(d, "atmosphere/config/user.ini")) == "user"); Assert(!File.Exists(Path.Combine(b, "atmosphere/config/user.ini"))); }));
Test("Read-only old files update and old Atmosphere core is removed", () => Scenario((s, d, b) => { Write(d, "atmosphere/package3", "old"); Write(d, "atmosphere/obsolete/core.bin"); File.SetAttributes(Path.Combine(d, "atmosphere/package3"), FileAttributes.ReadOnly); new Installer().Install(s, d, b); Assert(!Directory.Exists(Path.Combine(d, "atmosphere/obsolete"))); Assert(File.ReadAllText(Path.Combine(b, "atmosphere/package3")) == "old"); }));
Test("Bootloader INI settings survive update", () => Scenario((s, d, b) => { Write(s, "bootloader/ini/user.ini", "pack"); Write(d, "bootloader/ini/user.ini", "mine"); new Installer().Install(s, d, b); Assert(File.ReadAllText(Path.Combine(d, "bootloader/ini/user.ini")) == "mine"); }));
Test("Injected mid-install failure restores complete old tree", () => Scenario((s, d, b) => { Write(d, "atmosphere/package3", "old"); Write(d, "bootloader/ini/user.ini", "mine"); Write(d, "payload.bin", "old payload"); Write(s, "payload.bin", "new payload"); Write(s, "newfolder/newfile", "new"); var before = Snapshot(d); int copies = 0; var engine = new Installer { CopyFile = (a, z) => { if (++copies == 3) throw new IOException("simulated write failure"); VerifiedFiles.Copy(a, z); } }; Throws(() => engine.Install(s, d, b)); EqualSnapshot(before, d); Assert(!Directory.Exists(Path.Combine(d, "newfolder"))); }));
Test("Cancellation after verified backup makes no SD changes", () => Scenario((s, d, b) => { Write(d, "atmosphere/package3", "old"); var before = Snapshot(d); using var cancel = new CancellationTokenSource(); Throws(() => new Installer().Install(s, d, b, progress: p => cancel.Cancel(), cancel: cancel.Token)); EqualSnapshot(before, d); }));
Test("Drive validation failure stops installation before changes", () => Scenario((s, d, b) => { Write(d, "atmosphere/package3", "old"); var before = Snapshot(d); Throws(() => new Installer().Install(s, d, b, validateTarget: () => throw new IOException("changed card"))); EqualSnapshot(before, d); }));
Test("Backup inside SD and overlapping source rejected", () => Scenario((s, d, b) => { Throws(() => new Installer().Install(s, d, Path.Combine(d, "backup"))); Throws(() => InstallPolicy.Plan(s, s)); }));
Test("Linked SD paths cannot redirect writes outside card", () => Scenario((s, d, b) => {
    var external = Path.Combine(Path.GetDirectoryName(d)!, "external"); Directory.CreateDirectory(external); Write(external, "keep", "unchanged");
    var link = Path.Combine(d, "bootloader");
    if (OperatingSystem.IsWindows()) {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(link); start.ArgumentList.Add(external);
        using var process = System.Diagnostics.Process.Start(start)!; process.StandardOutput.ReadToEnd(); process.WaitForExit(); Assert(process.ExitCode == 0);
    } else Directory.CreateSymbolicLink(link, external);
    Throws(() => InstallPolicy.Plan(s, d)); Assert(File.ReadAllText(Path.Combine(external, "keep")) == "unchanged");
}));
Test("Malformed NRO and spoofed authors are never utilities", () => Scenario((s, d, b) => { Nro(s, "bad.nro", "JKSV", "Wrong Author"); Assert(AppIdentifier.Read(Path.Combine(s, "bad.nro")) == null); Write(s, "bad.nro", "NRO0"); Assert(AppIdentifier.Read(Path.Combine(s, "bad.nro")) == null); }));
Test("Every reviewed utility identity can be recognised", () => Scenario((s, d, b) => { foreach (var group in Catalog.Current.Names) { Nro(s, "sample.nro", group[1], Catalog.Current.Authors[group[0]][0]); Assert(AppIdentifier.Read(Path.Combine(s, "sample.nro"))?.Utility == group[0], group[0]); } }));
Test("Overflowing NACP offsets fail safely", () => Scenario((s, d, b) => { Nro(s, "bad.nro"); var path = Path.Combine(s, "bad.nro"); var bytes = File.ReadAllBytes(path); BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x98), ulong.MaxValue); File.WriteAllBytes(path, bytes); Assert(AppIdentifier.Read(path) == null); }));
Test("Protected configuration rejects unsafe paths", () => Scenario((s, d, b) => { foreach (var path in new[] { "../outside", "C:/windows", "switch/../thing", "/absolute", "switch/NUL", "switch/name.", "switch/a:stream", "." }) { Write(s, "config.json", JsonSerializer.Serialize(new { preserve_paths = new[] { path } })); Throws(() => InstallPolicy.LoadPreserved(Path.Combine(s, "config.json"))); } }));

void ZipCase(string[] entries, Action<string, string> body)
{
    var temp = Scratch(); try { var zipPath = Path.Combine(temp, "pack.zip"); using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create)) foreach (var path in entries) { using var output = new StreamWriter(zip.CreateEntry(path).Open()); output.Write("data"); } body(zipPath, Path.Combine(temp, "out")); } finally { GC.KeepAlive(temp); }
}
Test("ZIP rejects traversal, aliases, reserved names and ADS", () => { foreach (var path in new[] { "../escape", "/absolute", "a/./bad", "a/../bad", "a:stream", "NUL.txt", "COM1", "a/trailing.", "a/trailing ", "a//b" }) ZipCase([path], (z, d) => { Throws(() => ArchiveService.Extract(z, d)); Assert(!Directory.Exists(d)); }); });
Test("ZIP duplicate case and file-folder conflicts rejected before extraction", () => { foreach (var pair in new[] { new[] { "A", "a" }, new[] { "a", "a/b" } }) ZipCase(pair, (z, d) => { Throws(() => ArchiveService.Extract(z, d)); Assert(!Directory.Exists(d)); }); });
Test("ZIP valid contents extract and verify", () => ZipCase(["a/b.txt", "c.txt"], (z, d) => { ArchiveService.Extract(z, d); Assert(File.ReadAllText(Path.Combine(d, "a/b.txt")) == "data"); }));
Test("ZIP symlink rejected", () => { var temp = Scratch(); try { var path = Path.Combine(temp, "link.zip"); using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) zip.CreateEntry("link").ExternalAttributes = unchecked((int)0xA1FF0000); Throws(() => ArchiveService.Extract(path, Path.Combine(temp, "out"))); } finally { GC.KeepAlive(temp); } });
Test("ZIP CRC corruption rejected", () => { var temp = Scratch(); try { var path = Path.Combine(temp, "crc.zip"); using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) { using var output = new StreamWriter(zip.CreateEntry("file", CompressionLevel.NoCompression).Open()); output.Write("UNIQUE_DATA"); } var bytes = File.ReadAllBytes(path); int at = Encoding.UTF8.GetString(bytes).IndexOf("UNIQUE_DATA", StringComparison.Ordinal); Assert(at >= 0); bytes[at] ^= 1; File.WriteAllBytes(path, bytes); Throws(() => ArchiveService.Extract(path, Path.Combine(temp, "out"))); } finally { GC.KeepAlive(temp); } });

string ReleaseJson(string tag = "anything-v999", string filename = "any new name+version.zip", string? digest = null, bool prerelease = false, string state = "uploaded", string host = "github.com") => JsonSerializer.Serialize(new { id = 44, name = "Latest HATS", tag_name = tag, html_url = "https://github.com/TechRepairs4U/HATS/releases", published_at = "2026-10-23", draft = false, prerelease, assets = new[] { new { id = 42, name = filename, state, browser_download_url = $"https://{host}/TechRepairs4U/HATS/releases/download/{tag}/{Uri.EscapeDataString(filename)}", size = 4, digest, updated_at = "now" } } });
Test("Release uses arbitrary tag and asset name exactly as returned", () => { var r = ReleaseService.Parse(ReleaseJson()); Assert(r.Tag == "anything-v999" && r.Assets[0].Name == "any new name+version.zip" && r.Assets[0].Url.Contains("%2B")); });
Test("Release parser rejects prerelease, unavailable and external assets", () => { Throws(() => ReleaseService.Parse(ReleaseJson(prerelease: true))); Throws(() => ReleaseService.Parse(ReleaseJson(state: "new"))); Throws(() => ReleaseService.Parse(ReleaseJson(host: "example.com"))); Throws(() => ReleaseService.Parse(ReleaseJson(filename: "pack.exe"))); });
Test("Release recheck detects asset replacement even with same name", () => { var r = ReleaseService.Parse(ReleaseJson()); ReleaseService.Confirm(r, r.Assets[0], r); var changed = r with { Assets = [r.Assets[0] with { Digest = "sha256:changed" }] }; Throws(() => ReleaseService.Confirm(r, r.Assets[0], changed)); Throws(() => ReleaseService.Confirm(r, r.Assets[0], r with { Id = 45 })); });
Test("Download verifies size, digest and removes partial files", () => { var temp = Scratch(); try { var payload = "data"u8.ToArray(); var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(payload)); using var service = new ReleaseService(new FakeHttp(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) })); var asset = ReleaseService.Parse(ReleaseJson(digest: digest)).Assets[0]; var path = Path.Combine(temp, "ok.zip"); service.DownloadAsync(asset, path).GetAwaiter().GetResult(); Assert(File.ReadAllText(path) == "data"); var bad = Path.Combine(temp, "bad.zip"); Throws(() => service.DownloadAsync(asset with { Size = 5 }, bad).GetAwaiter().GetResult()); Assert(!File.Exists(bad)); Throws(() => service.DownloadAsync(asset with { Digest = "sha256:bad" }, bad).GetAwaiter().GetResult()); Assert(!File.Exists(bad)); } finally { GC.KeepAlive(temp); } });
Test("GitHub rate limits report a friendly error", () => { using var service = new ReleaseService(new FakeHttp(() => new HttpResponseMessage(HttpStatusCode.TooManyRequests))); try { service.LatestAsync().GetAwaiter().GetResult(); throw new Exception("Expected failure"); } catch (IOException e) { Assert(e.Message.Contains("limiting")); } });

string ExclusionJson(params string[] paths) => JsonSerializer.Serialize(new { schema_version = 1, preserve_paths = paths });
HttpResponseMessage ExclusionResponse(string json, bool badHash = false, bool badSize = false)
{
    var data = Encoding.UTF8.GetBytes(json);
    var sha = Convert.ToHexString(SHA1.HashData(Encoding.ASCII.GetBytes($"blob {data.Length}\0").Concat(data).ToArray()));
    return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { type = "file", path = ExclusionService.FileName, encoding = "base64", size = data.Length + (badSize ? 1 : 0), content = Convert.ToBase64String(data), sha = badHash ? new string('0', 40) : sha })) };
}
Test("GitHub exclusions normalise paths and preserve ordinary folder names", () => {
    var paths = ExclusionService.Parse(ExclusionJson("switch/My Game", "SWITCH/my game", Gta));
    Assert(paths.Length == 2 && paths.Contains("switch/my game") && paths.Contains(Paths.Key(Gta)));
    Assert(ExclusionService.Parse(ExclusionJson()).Length == 0);
});
Test("GitHub exclusions reject missing, misspelled and ambiguous settings", () => {
    foreach (var json in new[] { "[]", "{}", "{\"schema_version\":2,\"preserve_paths\":[]}", "{\"schema_version\":1,\"preserve_paths\":[],\"preserve_paths\":[]}", "{\"schema_version\":1,\"preserve_paths\":[],\"exclude_paths\":[]}", "{\"schema_version\":1,\"preserve_paths\":[null]}", "{\"schema_version\":1,\"preserve_paths\":[],\"notes\":false}", "not JSON" }) Throws(() => ExclusionService.Parse(json));
});
Test("GitHub exclusions reject traversal, roots, wildcards and oversized lists", () => {
    foreach (var path in new[] { "../outside", "C:/Windows", "switch/../game", "/", "switch/*", "switch/NUL", "switch/data.", "switch/file:stream", "", new string('a', 513) }) Throws(() => ExclusionService.Parse(ExclusionJson(path)));
    Throws(() => ExclusionService.Parse(ExclusionJson(Enumerable.Repeat("switch/a", 4097).ToArray())));
    Throws(() => ExclusionService.Parse(new string(' ', ExclusionService.MaximumBytes + 1)));
});
Test("GitHub requests refresh default branch and never reuse a preview or cache", () => {
    var queries = new HashSet<string>(); int calls = 0;
    using var service = new ExclusionService(new InspectHttp(request => {
        Assert(request.RequestUri!.AbsolutePath == "/repos/TechRepairs4U/HATS/contents/hats-exclusions.json");
        Assert(!request.RequestUri.Query.Contains("ref=") && queries.Add(request.RequestUri.Query));
        Assert(request.Headers.CacheControl is { NoCache: true, NoStore: true });
        return ExclusionResponse(ExclusionJson(++calls == 1 ? "switch/first" : "switch/second"));
    }));
    var first = service.LatestAsync().GetAwaiter().GetResult(); var second = service.LatestAsync().GetAwaiter().GetResult();
    Assert(first.PreservePaths.Single() == "switch/first" && second.PreservePaths.Single() == "switch/second" && first.BlobSha != second.BlobSha && calls == 2);
});
Test("GitHub exclusions verify content hash, size and response bounds", () => {
    foreach (var response in new Func<HttpResponseMessage>[] { () => ExclusionResponse(ExclusionJson(), badHash: true), () => ExclusionResponse(ExclusionJson(), badSize: true), () => new(HttpStatusCode.OK) { Content = new StringContent(new string('x', 300000)) }, () => new(HttpStatusCode.NotModified), () => new(HttpStatusCode.OK) { Content = new StringContent("{}") } }) {
        using var service = new ExclusionService(new FakeHttp(response)); Throws(() => service.LatestAsync().GetAwaiter().GetResult());
    }
});
Test("Failed or malformed GitHub exclusions create no backup and leave SD untouched", () => {
    foreach (var response in new Func<HttpResponseMessage>[] { () => new(HttpStatusCode.NotFound), () => new(HttpStatusCode.Forbidden), () => new(HttpStatusCode.TooManyRequests), () => new(HttpStatusCode.ServiceUnavailable), () => ExclusionResponse("bad JSON"), () => ExclusionResponse(ExclusionJson("../bad")), () => throw new HttpRequestException("offline") }) Scenario((s, d, b) => {
        Write(d, "atmosphere/package3", "old"); var before = Snapshot(d);
        var installer = new Installer { CreateExclusionService = () => new(new FakeHttp(response)) };
        Throws(() => installer.InstallWithLatestExclusionsAsync(s, d, b).GetAwaiter().GetResult());
        EqualSnapshot(before, d); Assert(!Directory.Exists(b));
    });
});
Test("Cancellation during exclusion fetch stops before backup or deletion", () => Scenario((s, d, b) => {
    Write(d, "atmosphere/package3", "old"); var before = Snapshot(d); using var cancel = new CancellationTokenSource();
    var installer = new Installer { CreateExclusionService = () => new(new FakeHttp(() => { cancel.Cancel(); return ExclusionResponse(ExclusionJson()); })) };
    Throws(() => installer.InstallWithLatestExclusionsAsync(s, d, b, cancel: cancel.Token).GetAwaiter().GetResult());
    EqualSnapshot(before, d); Assert(!Directory.Exists(b));
}));
Test("Remote exclusions add to local and automatic rules before any backup", () => Scenario((s, d, b) => {
    Nro(s, "switch/JKSV/JKSV.nro", tail: "new"); Nro(d, "switch/JKSV/JKSV.nro", tail: "old");
    foreach (var p in new[] { "atmosphere/config/remote.ini", "atmosphere/config/local.ini", Gta + "/romfs/game.dat" }) { Write(s, p, "pack"); Write(d, p, "user"); }
    Write(s, Module + "/exefs.nsp", "new module"); Write(d, Module + "/exefs.nsp", "old module");
    Write(d, "atmosphere/package3", "old"); var before = Snapshot(d);
    var json = ExclusionJson("switch/JKSV", "atmosphere/config/remote.ini"); int requests = 0;
    var installer = new Installer { CreateExclusionService = () => new(new FakeHttp(() => { Assert(!Directory.Exists(b)); requests++; return ExclusionResponse(json); })) };
    var plan = installer.InstallWithLatestExclusionsAsync(s, d, b, ["atmosphere/config/local.ini"]).GetAwaiter().GetResult();
    foreach (var p in new[] { "switch/JKSV/JKSV.nro", "atmosphere/config/remote.ini", "atmosphere/config/local.ini", Gta + "/romfs/game.dat" }) { Assert(Hash(Path.Combine(d, p)) == before[p]); Assert(!File.Exists(Path.Combine(b, p))); Assert(!plan.Copy.Contains(p)); }
    Assert(requests == 1 && File.ReadAllText(Path.Combine(d, Module, "exefs.nsp")) == "new module");
    Assert(File.ReadAllText(Path.Combine(d, "atmosphere/package3")) == "new");
    Assert(File.ReadAllText(Path.Combine(b, "HATS-exclusions-used.json")) == json);
    using var recovery = JsonDocument.Parse(File.ReadAllText(Path.Combine(b, "HATS-recovery-plan.json")));
    Assert(recovery.RootElement.GetProperty("GitHubExclusions").GetProperty("File").GetString() == ExclusionService.FileName);
}));
Test("An empty GitHub list cannot remove built-in protections", () => Scenario((s, d, b) => {
    Write(s, Gta + "/exefs.nsp", "pack"); Write(d, Gta + "/exefs.nsp", "user");
    var installer = new Installer { CreateExclusionService = () => new(new FakeHttp(() => ExclusionResponse(ExclusionJson()))) };
    installer.InstallWithLatestExclusionsAsync(s, d, b).GetAwaiter().GetResult();
    Assert(File.ReadAllText(Path.Combine(d, Gta, "exefs.nsp")) == "user" && !Directory.Exists(Path.Combine(b, Gta)));
}));

var packIndex = Array.IndexOf(args, "--real-pack");
if (packIndex >= 0 && args.Length > packIndex + 1)
{
    var packPath = Path.GetFullPath(args[packIndex + 1]);
    Test("Real HATS pack: all utility identities update; ordinary ports/mods preserved", () => {
        var temp = Scratch(); try {
            var extracted = Path.Combine(temp, "extracted"); ArchiveService.Extract(packPath, extracted); var source = InstallPolicy.FindPack(extracted); var sd = Path.Combine(temp, "sd"); Directory.CreateDirectory(sd); var backup = Path.Combine(temp, "backup");
            var apps = Directory.EnumerateFiles(Path.Combine(source, "switch"), "*", SearchOption.AllDirectories).Where(p => p.EndsWith(".nro", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".ovl", StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var app in apps) { Assert(AppIdentifier.Read(app) != null, "Unrecognised real pack utility: " + app); var relative = Paths.Relative(source, app); var target = Path.Combine(sd, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(app, target); using var tail = new FileStream(target, FileMode.Append); tail.Write("old version"u8); }
            var protectedPaths = new[] { Gta + "/romfs/data.dat", "atmosphere/contents/0100999988887777/romfs/mod.dat", "switch/random name/game-data.bin", "switch/Grand Theft Auto V/GTA.nro", "switch/DOOM/doom.wad", "atmosphere/automatic_backups/unique.keys" };
            foreach (var path in protectedPaths) Write(sd, path, "user's content");
            var stamps = protectedPaths.ToDictionary(p => p, p => File.GetLastWriteTimeUtc(Path.Combine(sd, p)));
            Write(sd, "atmosphere/package3", "old core"); Write(sd, Module + "/exefs.nsp", "old module");
            InstallPlan plan;
            if (args.Contains("--live-exclusions"))
            {
                var before = Snapshot(sd); bool fetched = false, backedUp = false, checkedBeforeDeletion = false;
                var order = new List<string>();
                plan = new Installer().InstallWithLatestExclusionsAsync(source, sd, backup,
                    log: message => {
                        if (message.StartsWith("GitHub exclusions:"))
                        {
                            Assert(!Directory.Exists(backup), "Backup was created before the config was fetched");
                            EqualSnapshot(before, sd); fetched = true; order.Add("Latest GitHub config fetched and validated");
                            Console.WriteLine("  " + message);
                        }
                        if (message.StartsWith("Backing up ")) Assert(fetched, "Backup started before config validation");
                    },
                    progress: p => {
                        if (p.Phase == "Refreshing GitHub exclusions") Assert(!Directory.Exists(backup));
                        if (p.Phase == "Backing up")
                        {
                            Assert(fetched && File.Exists(Path.Combine(backup, "HATS-exclusions-used.json")));
                            Assert(File.ReadAllText(Path.Combine(sd, "atmosphere/package3")) == "old core");
                            if (!backedUp) order.Add("Backups started with the validated exclusions");
                            backedUp = true;
                        }
                        if (p.Phase == "Installing" && p.Percent == 35)
                        {
                            Assert(fetched && backedUp); EqualSnapshot(before, sd);
                            Assert(File.ReadAllText(Path.Combine(backup, "atmosphere/package3")) == "old core");
                            checkedBeforeDeletion = true; order.Add("Verified backups complete; SD unchanged before deletion");
                        }
                    },
                    validateTarget: () => { if (Directory.Exists(backup)) Assert(fetched); }).GetAwaiter().GetResult();
                Assert(fetched && backedUp && checkedBeforeDeletion);
                using var recovery = JsonDocument.Parse(File.ReadAllText(Path.Combine(backup, "HATS-recovery-plan.json")));
                var snapshot = recovery.RootElement.GetProperty("GitHubExclusions").Deserialize<ExclusionSnapshot>()!;
                Assert(snapshot.Repository == ReleaseService.Repository && snapshot.File == ExclusionService.FileName);
                Assert(snapshot.PreservePaths.All(p => plan.Preserved.Contains(p, StringComparer.OrdinalIgnoreCase)));
                Assert(File.ReadAllText(Path.Combine(backup, "HATS-exclusions-used.json")) == snapshot.Json);
                int reportArg = Array.IndexOf(args, "--verification-report");
                if (reportArg >= 0 && reportArg + 1 < args.Length)
                    File.WriteAllText(args[reportArg + 1], JsonSerializer.Serialize(new { Snapshot = snapshot, Order = order, ConfigBeforeBackup = fetched, VerifiedBackupBeforeDeletion = checkedBeforeDeletion, UsedRealHatsPack = true, UsedPhysicalCard = false }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("  Verified order: " + string.Join(" -> ", order));
            }
            else plan = new Installer().Install(source, sd, backup);
            foreach (var app in apps) { var relative = Paths.Relative(source, app); Assert(Hash(app) == Hash(Path.Combine(sd, relative)), "Not updated: " + relative); Assert(File.Exists(Path.Combine(backup, relative)), "No backup: " + relative); }
            foreach (var path in protectedPaths) { Assert(File.ReadAllText(Path.Combine(sd, path)) == "user's content"); Assert(File.GetLastWriteTimeUtc(Path.Combine(sd, path)) == stamps[path]); Assert(!File.Exists(Path.Combine(backup, path))); }
            Assert(plan.Sysmodules.Length == 16, "Unexpected sysmodule count"); Console.WriteLine($"  Real pack: {apps.Length} recognised apps/overlays, {plan.Sysmodules.Length} sysmodules, {plan.Copy.Length} files; protected content and timestamps unchanged.");
        } finally { GC.KeepAlive(temp); }
    });
}
int failed = 0;
foreach (var test in tests) { try { test.Body(); Console.WriteLine("PASS " + test.Name); } catch (Exception e) { failed++; Console.WriteLine("FAIL " + test.Name + "\n" + e); } }
if (args.Contains("--live"))
{
    try { using var service = new ReleaseService(); var latest = await service.LatestAsync(); Console.WriteLine($"LIVE: {latest.Title}; tag={latest.Tag}; ZIP assets={latest.Assets.Length}"); } catch (Exception e) { failed++; Console.WriteLine("FAIL live release: " + e.Message); }
}
Console.WriteLine($"RESULT: {tests.Count - failed}/{tests.Count} passed; {failed} failures.");
return failed == 0 ? 0 : 1;


} }
sealed class FakeHttp(Func<HttpResponseMessage> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response());
}
sealed class InspectHttp(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
}

