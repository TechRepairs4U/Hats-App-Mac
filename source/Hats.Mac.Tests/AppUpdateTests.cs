using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hats.Core;

static class AppUpdateTests
{
    static readonly byte[] Payload = "fake download used only by tests"u8.ToArray();
    static string Digest(byte[] bytes) => "sha256:" + Convert.ToHexString(SHA256.HashData(bytes));
    static void Assert(bool value) { if (!value) throw new Exception("App updater assertion failed"); }
    static void Throws(Action action) { try { action(); } catch { return; } throw new Exception("Expected update failure"); }
    static string Json(string tag = "v2.1.9", bool draft = false, bool prerelease = false,
        string? name = null, string? url = null, string? digest = null, long? size = null, string state = "uploaded", bool duplicate = false)
    {
        var asset = new { id = 44, name = name ?? AppUpdateService.AssetName,
            browser_download_url = url ?? $"https://github.com/{AppUpdateService.Repository}/releases/download/{tag}/{AppUpdateService.AssetName}",
            size = size ?? Payload.Length, digest = digest ?? Digest(Payload), state, updated_at = "now" };
        return JsonSerializer.Serialize(new { id = 42, tag_name = tag, body = "Release notes", draft, prerelease, assets = duplicate ? new[] { asset, asset } : new[] { asset } });
    }
    static void Scratch(Action<string> body)
    {
        var path = Path.Combine(Path.GetTempPath(), "hats-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); try { body(path); } finally { Directory.Delete(path, true); }
    }
    static AppUpdateService Service(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(new Handler(respond));
    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }
    public static void Register(Action<string, Action> test, string[] args)
    {
        test("App versions compare numerically and normalize v-prefixed release tags", () => {
            Assert(AppUpdateService.ParseVersion("v2.1.10") > AppUpdateService.ParseVersion("2.1.9"));
            Assert(AppUpdateService.ParseVersion("V2.0") == AppUpdateService.ParseVersion("2.0.0.0"));
            Assert(AppUpdateService.ParseVersion("macos-v2.1.9") == new Version(2, 1, 9, 0));
            Assert(AppUpdateService.Parse(Json()).DisplayVersion == "2.1.9");
        });
        test("App updater rejects unstable or ambiguous version tags", () => {
            foreach (var value in new[] { "v2.1.9-beta", "hats-installer", "", "v", "2.1.9 ", "2.1.9/evil", "2.-1.9" })
                Throws(() => AppUpdateService.ParseVersion(value));
        });
        test("App update feed selects only the exact stable universal Mac app asset", () => {
            var release = AppUpdateService.Parse(Json());
            Assert(release.Version == new Version(2, 1, 9, 0) && release.Asset.Name == AppUpdateService.AssetName);
            foreach (var name in new[] { "HATS.MM.zip", "Magic-Monkei-HATS-Installer.x64.exe", "Magic-Monkei-HATS-Installer.arm64.exe", "source.zip" })
                Throws(() => AppUpdateService.Parse(Json(name: name)));
            Throws(() => AppUpdateService.Parse(Json(duplicate: true)));
            Throws(() => AppUpdateService.Parse(Json(draft: true)));
            Throws(() => AppUpdateService.Parse(Json(prerelease: true)));
        });
        test("App updater requires an official release URL and SHA-256 digest", () => {
            foreach (var url in new[] { "http://github.com/evil.exe", "https://github.com.evil.test/update.exe", "https://github.com/other/repo/releases/download/v2.1.9/update.exe", "file:///C:/bad.exe" })
                Throws(() => AppUpdateService.Parse(Json(url: url)));
            foreach (var digest in new[] { "", "sha256:short", "sha1:" + new string('a', 64), "sha256:" + new string('z', 64) })
                Throws(() => AppUpdateService.Parse(Json(digest: digest)));
            Throws(() => AppUpdateService.Parse(Json(size: 0)));
            Throws(() => AppUpdateService.Parse(Json(size: AppUpdateService.MaximumBytes + 1)));
            Throws(() => AppUpdateService.Parse(Json(state: "new")));
        });
        test("App updater checks the app repository, handles missing releases and rate limits", () => {
            using var ok = Service(request => {
                Assert(request.RequestUri!.AbsolutePath == "/repos/TechRepairs4U/Hats-App-Mac/releases" && request.RequestUri.Query.Contains("per_page=100"));
                return new(HttpStatusCode.OK) { Content = new StringContent("[" + Json() + "]") };
            });
            Assert(ok.LatestAsync().GetAwaiter().GetResult()!.Version == new Version(2, 1, 9, 0));
            using var missing = Service(_ => new(HttpStatusCode.NotFound)); Assert(missing.LatestAsync().GetAwaiter().GetResult() == null);
            using var limited = Service(_ => new(HttpStatusCode.TooManyRequests)); Throws(() => limited.LatestAsync().GetAwaiter().GetResult());
        });
        test("Mixed repository releases cannot replace the app with a Switch or prerelease build", () => {
            using var service = Service(_ => new(HttpStatusCode.OK) { Content = new StringContent("[" + string.Join(",", new[] {
                Json(tag: "v99.0.0", name: "MM-HATS-INSTALLER.nsp"), Json(tag: "v99.1.0", prerelease: true),
                Json(tag: "v3.0.0", draft: true), Json(tag: "v2.0"), Json(tag: "macos-v2.1.9") }) + "]") });
            Assert(service.LatestAsync().GetAwaiter().GetResult()!.Version == new Version(2, 1, 9, 0));
        });
        test("App release lookup follows pages rather than stopping at unrelated releases", () => {
            int calls = 0;
            using var service = Service(request => {
                calls++;
                Assert(request.RequestUri!.Query.Contains("page=" + calls));
                var releases = calls == 1 ? Enumerable.Repeat(Json(name: "Switch-only.zip"), 100) : new[] { Json() };
                return new(HttpStatusCode.OK) { Content = new StringContent("[" + string.Join(",", releases) + "]") };
            });
            Assert(service.LatestAsync().GetAwaiter().GetResult()!.Version == new Version(2, 1, 9, 0) && calls == 2);
        });
        test("App release is rechecked before downloading and changed assets are rejected", () => {
            var release = AppUpdateService.Parse(Json()); AppUpdateService.Confirm(release, release);
            Throws(() => AppUpdateService.Confirm(release, null));
            Throws(() => AppUpdateService.Confirm(release, release with { Id = 43 }));
            Throws(() => AppUpdateService.Confirm(release, release with { Asset = release.Asset with { Digest = Digest([1, 2]) } }));
        });
        test("App download verifies exact size and checksum", () => Scratch(folder => {
            using var service = Service(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });
            var path = Path.Combine(folder, "update.exe"); var progress = new List<double>();
            service.DownloadAsync(AppUpdateService.Parse(Json()), path, progress.Add).GetAwaiter().GetResult();
            Assert(File.ReadAllBytes(path).SequenceEqual(Payload) && progress.Last() == 100);
        }));
        test("Bad or incomplete app downloads are removed", () => Scratch(folder => {
            foreach (var release in new[] { AppUpdateService.Parse(Json(size: Payload.Length + 1)), AppUpdateService.Parse(Json(size: Payload.Length - 1)), AppUpdateService.Parse(Json(digest: Digest([1, 2, 3]))) }) {
                using var service = Service(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });
                var path = Path.Combine(folder, Guid.NewGuid() + ".exe");
                Throws(() => service.DownloadAsync(release, path).GetAwaiter().GetResult()); Assert(!File.Exists(path));
            }
        }));
        test("Failed download cannot delete a pre-existing destination", () => Scratch(folder => {
            var path = Path.Combine(folder, "existing.exe"); File.WriteAllText(path, "keep me");
            using var service = Service(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });
            Throws(() => service.DownloadAsync(AppUpdateService.Parse(Json()), path).GetAwaiter().GetResult());
            Assert(File.ReadAllText(path) == "keep me");
        }));
        test("Cancelled app download and external redirect cannot reach replacement", () => Scratch(folder => {
            var path = Path.Combine(folder, "update.exe"); using var stop = new CancellationTokenSource(); stop.Cancel();
            using var service = Service(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });
            Throws(() => service.DownloadAsync(AppUpdateService.Parse(Json()), path, cancel: stop.Token).GetAwaiter().GetResult()); Assert(!File.Exists(path));
            using var redirected = Service(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload), RequestMessage = new(HttpMethod.Get, "https://evil.test/app.exe") });
            Throws(() => redirected.DownloadAsync(AppUpdateService.Parse(Json()), path).GetAwaiter().GetResult()); Assert(!File.Exists(path));
        }));

    }
}

