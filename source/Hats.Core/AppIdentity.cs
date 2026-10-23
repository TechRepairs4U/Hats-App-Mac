using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hats.Core;

public sealed class Catalog
{
    public string[][] Names { get; set; } = [];
    public Dictionary<string, string[]> Authors { get; set; } = [];
    public HashSet<string> GameNames { get; set; } = [];
    public HashSet<string> GameFiles { get; set; } = [];
    public Dictionary<string, string> Modules { get; set; } = [];
    public string[] Defaults { get; set; } = [];
    public static Catalog Current { get; } = Load();
    static Catalog Load()
    {
        using var input = typeof(Catalog).Assembly.GetManifestResourceStream("Hats.Core.catalog.json")!;
        return JsonSerializer.Deserialize<Catalog>(input, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
}

public sealed record AppIdentity(string Utility, string Title, string Author);

public static class AppIdentifier
{
    static readonly Dictionary<string, string> Ids = Catalog.Current.Names.SelectMany(g => g.Skip(1).Select(n => (Name: Normalize(n), Id: g[0]))).GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.First().Id);
    static readonly UTF8Encoding Utf8 = new(false, true);
    public static string Normalize(string text) => string.Concat(text.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Where(char.IsLetterOrDigit));
    static string Decode(ReadOnlySpan<byte> bytes) { var end = bytes.IndexOf((byte)0); return Utf8.GetString(end < 0 ? bytes : bytes[..end]).Trim(); }

    // Read only the NRO header, asset header and language metadata (12.5 KiB).
    public static AppIdentity? Read(string path)
    {
        try
        {
            if (Paths.Linked(path)) return null;
            using var file = File.OpenRead(path);
            Span<byte> header = stackalloc byte[0x80]; file.ReadExactly(header);
            if (!header.Slice(0x10, 4).SequenceEqual("NRO0"u8)) return null;
            long asset = BinaryPrimitives.ReadUInt32LittleEndian(header[0x18..]);
            if (asset < 0x80 || asset > file.Length - 0x38) return null;
            file.Position = asset;
            Span<byte> assets = stackalloc byte[0x38]; file.ReadExactly(assets);
            if (!assets[..4].SequenceEqual("ASET"u8) || BinaryPrimitives.ReadUInt32LittleEndian(assets[4..]) != 0) return null;
            ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(assets[0x18..]), size = BinaryPrimitives.ReadUInt64LittleEndian(assets[0x20..]);
            ulong remaining = (ulong)(file.Length - asset);
            if (offset < 0x38 || size < 0x3000 || offset > remaining || size > remaining - offset) return null;
            file.Position = asset + (long)offset;
            var data = new byte[0x3000]; file.ReadExactly(data);
            AppIdentity? result = null;
            for (int pos = 0; pos < data.Length; pos += 0x300)
            {
                var title = Decode(data.AsSpan(pos, 0x200));
                if (title.Length == 0) continue;
                var author = Decode(data.AsSpan(pos + 0x200, 0x100));
                if (!Ids.TryGetValue(Normalize(title), out var id) || !Catalog.Current.Authors[id].Contains(author, StringComparer.OrdinalIgnoreCase)) continue;
                if (result != null && result.Utility != id) return null;
                result = new(id, title, author);
            }
            return result;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    public static string? GameFolderReason(string folder)
    {
        if (Catalog.Current.GameNames.Contains(Normalize(Path.GetFileName(folder)))) return "game, port or emulator folder";
        try
        {
            if (Paths.Linked(folder)) return "linked folder";
            if (Directory.Exists(folder))
                foreach (var child in Directory.EnumerateFileSystemEntries(folder))
                {
                    var name = Path.GetFileName(child).ToLowerInvariant();
                    if (Catalog.Current.GameFiles.Contains(name) || Regex.IsMatch(name, @"^(?:pak\d+\.(?:pak|pk3|pk4)|.*\.(?:wad|grp|otr|o2r|rsdk))$")) return "game data detected";
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "folder could not be identified safely"; }
        return null;
    }
}
