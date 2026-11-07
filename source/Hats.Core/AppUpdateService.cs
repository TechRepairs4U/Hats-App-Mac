using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Hats.Core;

public sealed record AppRelease(long Id, Version Version, string Tag, string Notes, PackAsset Asset)
{
    public string PageUrl => $"https://github.com/{AppUpdateService.Repository}/releases/tag/{Uri.EscapeDataString(Tag)}";
    public string DisplayVersion => Version.ToString(Version.Revision > 0 ? 4 : 3);
}

public sealed class AppUpdateService : IDisposable
{
    public const string Repository = "TechRepairs4U/Hats-App-Mac";
    public const string AssetName = "Magic-Monkei-HATS-Universal.dmg";
    public const long MaximumBytes = 512L * 1048576;
    readonly HttpClient client;
    public AppUpdateService(HttpMessageHandler? handler = null)
    {
        client = handler == null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.MaxResponseContentBufferSize = 2 * 1048576;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MagicMonkei-AppUpdater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }
    public static Version ParseVersion(string text)
    {
        if (text.StartsWith("macos-", StringComparison.OrdinalIgnoreCase)) text = text[6..];
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        if (!Version.TryParse(text, out var version) || version.Build < -1 || text.Any(c => !char.IsAsciiDigit(c) && c != '.'))
            throw new InvalidDataException("The app release does not have a stable version number.");
        return new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    }
    public async Task<AppRelease?> LatestAsync(CancellationToken cancel = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        AppRelease? newest = null;
        for (int page = 1; page <= 10; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases?per_page=100&page={page}");
            request.Headers.CacheControl = new() { NoCache = true };
            using var response = await client.SendAsync(request, deadline.Token);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new IOException("GitHub is temporarily limiting app update checks. Try again later.");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.GetProperty("draft").GetBoolean() || item.GetProperty("prerelease").GetBoolean() ||
                    !item.GetProperty("assets").EnumerateArray().Any(a => a.GetProperty("name").GetString() == AssetName)) continue;
                var release = Parse(item.GetRawText());
                if (newest == null || release.Version > newest.Version) newest = release;
            }
            if (document.RootElement.GetArrayLength() < 100) return newest;
        }
        throw new IOException("The app release history is too large to check completely. Try again later.");
    }
    public static AppRelease Parse(string json)
    {
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("Only published stable app releases can be installed.");
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = ParseVersion(tag);
        var matches = root.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == AssetName).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("The app release does not contain one universal macOS disk image.");
        var asset = matches[0];
        var url = asset.GetProperty("browser_download_url").GetString() ?? "";
        var expectedUrl = $"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{AssetName}";
        if (url != expectedUrl || asset.GetProperty("state").GetString() != "uploaded")
            throw new InvalidDataException("The app update download is not a published asset from the installer repository.");
        var size = asset.GetProperty("size").GetInt64();
        if (size <= 0 || size > MaximumBytes) throw new InvalidDataException("The app update size is invalid.");
        var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
        if (digest == null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || digest.Length != 71 || !digest[7..].All(char.IsAsciiHexDigit))
            throw new InvalidDataException("GitHub has not provided a SHA-256 checksum for this app update. Try again later.");
        var notes = root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString()! : "";
        return new(root.GetProperty("id").GetInt64(), version, tag, notes,
            new(asset.GetProperty("id").GetInt64(), AssetName, url, size, digest,
                asset.TryGetProperty("updated_at", out var updated) ? updated.GetString() ?? "" : ""));
    }
    public static void Confirm(AppRelease selected, AppRelease? latest)
    {
        if (latest == null || selected.Id != latest.Id || selected.Version != latest.Version || selected.Asset != latest.Asset)
            throw new IOException("The app release changed. Check for updates again before downloading.");
    }
    public async Task DownloadAsync(AppRelease release, string destination, Action<double>? progress = null, CancellationToken cancel = default)
    {
        bool created = false;
        try
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancel); idle.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await client.GetAsync(release.Asset.Url, HttpCompletionOption.ResponseHeadersRead, idle.Token);
            response.EnsureSuccessStatusCode();
            var finalUrl = response.RequestMessage?.RequestUri;
            if (finalUrl != null && (finalUrl.Scheme != "https" || !new[] { "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com" }.Contains(finalUrl.Host)))
                throw new IOException("The app download redirected outside GitHub's download servers.");
            await using var input = await response.Content.ReadAsStreamAsync(idle.Token);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1048576, true);
            created = true;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1048576]; long total = 0;
            while (true)
            {
                idle.CancelAfter(TimeSpan.FromSeconds(30));
                int count = await input.ReadAsync(buffer, idle.Token); if (count == 0) break;
                total = checked(total + count);
                if (total > release.Asset.Size) throw new InvalidDataException("The app download exceeded its expected size.");
                hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), cancel);
                progress?.Invoke(total * 100d / release.Asset.Size);
            }
            if (total != release.Asset.Size || response.Content.Headers.ContentLength is long length && total != length)
                throw new IOException("The app download was incomplete. The installed app was not changed.");
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Asset.Digest![7..], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The app download failed its SHA-256 check. The installed app was not changed.");
            await output.FlushAsync(cancel); output.Flush(true);
        }
        catch { if (created && File.Exists(destination)) File.Delete(destination); throw; }
    }
    public void Dispose() => client.Dispose();
}
