using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hats.Core;

public sealed record ExclusionSnapshot(string Repository, string File, string BlobSha, string Sha256, DateTime FetchedUtc, string[] PreservePaths, string Json);

public sealed class ExclusionService : IDisposable
{
    public const string FileName = "hats-exclusions.json";
    public const string ViewUrl = "https://github.com/" + ReleaseService.Repository + "/blob/HEAD/" + FileName;
    public const int MaximumBytes = 128 * 1024;
    readonly HttpClient client;
    public ExclusionService(HttpMessageHandler? handler = null)
    {
        client = handler == null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MagicMonkei-HATS/2.1.0");
    }

    public static string[] Parse(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new InvalidDataException("The exclusion list is too large (maximum 128 KB).");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The exclusion file must be a JSON object.");
        var names = new HashSet<string>();
        foreach (var field in root.EnumerateObject())
        {
            if (!names.Add(field.Name)) throw new InvalidDataException("Duplicate exclusion setting: " + field.Name);
            if (field.Name is not ("schema_version" or "preserve_paths" or "notes")) throw new InvalidDataException("Unknown exclusion setting: " + field.Name);
        }
        if (!root.TryGetProperty("schema_version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number) || number != 1)
            throw new InvalidDataException("The exclusion file requires schema_version: 1.");
        if (root.TryGetProperty("notes", out var notes) && notes.ValueKind != JsonValueKind.String) throw new InvalidDataException("Exclusion notes must be text.");
        if (!root.TryGetProperty("preserve_paths", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 4096)
            throw new InvalidDataException("The exclusion file requires a preserve_paths list with at most 4096 paths.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) throw new InvalidDataException("Each protected path must be text.");
            var path = item.GetString()!;
            if (path.Length > 512) throw new InvalidDataException("An exclusion path is too long (maximum 512 characters).");
            Paths.ValidateRelative(path); paths.Add(Paths.Key(path));
        }
        return paths.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<ExclusionSnapshot> LatestAsync(CancellationToken cancel = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            // Omitting ref follows the repository's current default branch, independently of release tags.
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{ReleaseService.Repository}/contents/{FileName}?refresh={Guid.NewGuid():N}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            request.Headers.CacheControl = new() { NoCache = true, NoStore = true };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.NotFound) throw new IOException($"{FileName} was not found in {ReleaseService.Repository} on its default branch.");
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) throw new IOException("GitHub is limiting requests. Try again later.");
            if (response.StatusCode != HttpStatusCode.OK) throw new IOException($"GitHub returned HTTP {(int)response.StatusCode}.");
            const int responseLimit = MaximumBytes * 2 + 16384;
            if (response.Content.Headers.ContentLength > responseLimit) throw new InvalidDataException("The GitHub exclusion response is too large.");
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var body = new MemoryStream(); var buffer = new byte[8192];
            while (true)
            {
                int count = await input.ReadAsync(buffer, deadline.Token); if (count == 0) break;
                if (body.Length + count > responseLimit) throw new InvalidDataException("The GitHub exclusion response is too large.");
                body.Write(buffer, 0, count);
            }
            using var document = JsonDocument.Parse(body.ToArray()); var file = document.RootElement;
            if (file.GetProperty("type").GetString() != "file" || file.GetProperty("path").GetString() != FileName || file.GetProperty("encoding").GetString() != "base64")
                throw new InvalidDataException("GitHub did not return the expected exclusion file.");
            int size = file.GetProperty("size").GetInt32();
            if (size < 1 || size > MaximumBytes) throw new InvalidDataException("The exclusion file must be between 1 byte and 128 KB.");
            var data = Convert.FromBase64String(file.GetProperty("content").GetString()!);
            if (data.Length != size) throw new InvalidDataException("The exclusion file was incomplete.");
            var sha = file.GetProperty("sha").GetString()!;
            // Git's blob identifier verifies that the metadata and decoded content describe the same file.
            using var blob = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            blob.AppendData(Encoding.ASCII.GetBytes($"blob {data.Length}\0")); blob.AppendData(data);
            if (!Convert.ToHexString(blob.GetHashAndReset()).Equals(sha, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The exclusion file failed its GitHub content check.");
            var json = new UTF8Encoding(false, true).GetString(data); var paths = Parse(json.TrimStart('\uFEFF'));
            cancel.ThrowIfCancellationRequested();
            return new(ReleaseService.Repository, FileName, sha, Convert.ToHexString(SHA256.HashData(data)), DateTime.UtcNow, paths, json);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is IOException or InvalidDataException or HttpRequestException or JsonException or FormatException or InvalidOperationException or KeyNotFoundException or DecoderFallbackException or OperationCanceledException)
        {
            var reason = e is OperationCanceledException ? "The request timed out." : e.Message;
            throw new IOException("Could not load the latest GitHub exclusions. No backup or SD card changes were started. " + reason + " Check Options > GitHub exclusions, then try again.", e);
        }
    }
    public void Dispose() => client.Dispose();
}
