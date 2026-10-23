using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Hats.Core;

public sealed record PackAsset(long Id, string Name, string Url, long Size, string? Digest, string UpdatedAt)
{
    public override string ToString() => $"{Name}  ·  {Size / 1048576d:N0} MB";
}
public sealed record HatsRelease(long Id, string Title, string Tag, string Url, string PublishedAt, PackAsset[] Assets);
public sealed class ReleaseChangedException() : IOException("The release or selected download has changed. Refresh releases and choose a pack again.");

public sealed class ReleaseService : IDisposable
{
    public const string Repository = "TechRepairs4U/HATS";
    readonly HttpClient client;
    public ReleaseService(HttpMessageHandler? handler = null)
    {
        client = handler == null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MagicMonkei-HATS/2.1.0");
    }
    public async Task<HatsRelease> LatestAsync(CancellationToken cancel = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28"); request.Headers.CacheControl = new() { NoCache = true };
        using var response = await client.SendAsync(request, deadline.Token);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) throw new IOException("GitHub is temporarily limiting requests. Please try again later.");
        if (response.StatusCode == HttpStatusCode.NotFound) throw new IOException("No published stable release was found in TechRepairs4U/HATS.");
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(deadline.Token));
    }
    public static HatsRelease Parse(string json)
    {
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) throw new InvalidDataException("Choose a published stable HATS release.");
        static string Text(JsonElement item, string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        var assets = new List<PackAsset>();
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = Text(a, "name"); var url = Text(a, "browser_download_url");
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || Text(a, "state") != "uploaded" || a.GetProperty("size").GetInt64() <= 0) continue;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || !uri.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.OrdinalIgnoreCase)) continue;
            assets.Add(new(a.GetProperty("id").GetInt64(), name, url, a.GetProperty("size").GetInt64(), string.IsNullOrEmpty(Text(a, "digest")) ? null : Text(a, "digest"), Text(a, "updated_at")));
        }
        if (assets.Count == 0) throw new InvalidDataException("The latest release has no downloadable ZIP packs. No older release was selected.");
        var tag = Text(root, "tag_name"); var title = Text(root, "name");
        return new(root.GetProperty("id").GetInt64(), title.Length == 0 ? tag : title, tag, Text(root, "html_url"), Text(root, "published_at"), assets.ToArray());
    }
    public static void Confirm(HatsRelease selectedRelease, PackAsset selected, HatsRelease latest)
    {
        if (selectedRelease.Id != latest.Id || !latest.Assets.Contains(selected)) throw new ReleaseChangedException();
    }
    public async Task DownloadAsync(PackAsset asset, string destination, Action<double>? progress = null, CancellationToken cancel = default)
    {
        try
        {
            if (Paths.FreeSpace(destination) < asset.Size + 16L * 1048576) throw new IOException("Not enough temporary disk space to download this HATS pack.");
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancel); idle.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, idle.Token); response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(idle.Token);
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1048576, true))
            {
                var buffer = new byte[1048576]; long total = 0;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                while (true)
                {
                    idle.CancelAfter(TimeSpan.FromSeconds(30));
                    int count = await input.ReadAsync(buffer, idle.Token); if (count == 0) break;
                    total = checked(total + count); if (total > asset.Size) throw new InvalidDataException("The download is larger than GitHub reported.");
                    hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), cancel); progress?.Invoke(total * 100d / asset.Size);
                }
                if (total != asset.Size || response.Content.Headers.ContentLength is long length && total != length) throw new IOException("The download was incomplete. Please try again.");
                var digest = Convert.ToHexString(hash.GetHashAndReset());
                if (asset.Digest != null)
                {
                    if (!asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || !asset.Digest[7..].Equals(digest, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The downloaded pack failed its GitHub checksum check. No SD files were changed.");
                }
                await output.FlushAsync(cancel); output.Flush(true);
            }
        }
        catch { if (File.Exists(destination)) File.Delete(destination); throw; }
    }
    public void Dispose() => client.Dispose();
}
