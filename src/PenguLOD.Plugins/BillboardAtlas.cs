using System.Security.Cryptography;
using PenguLOD.Core.Pipeline;
using PenguLOD.Core.World;
using PenguLOD.Textures;
using PenguLOD.Textures.Bc;
using PenguLOD.Textures.Dds;

namespace PenguLOD.Plugins;

/// <summary>
/// Packs billboards into one colour atlas and a normal-map atlas with the same layout, so every card that uses them shares one
/// material and a block draws them in one shape instead of one per billboard.
/// </summary>
public static class BillboardAtlas
{
    /// <summary>One billboard: the key its rectangle is found under, its colour texture and its normal map (null = flat).</summary>
    public sealed record Tile(string Key, string Diffuse, string? Normal);

    /// <param name="SizeCap">Largest tile side kept (int.MaxValue = nothing reduced).</param>
    /// <param name="Reduced">How many tiles were scaled down to fit.</param>
    /// <param name="Skipped">Billboards that could not be read.</param>
    /// <param name="Hash">Short hash of both atlases, for fingerprints of what is built from them.</param>
    public sealed record Result(byte[] Diffuse, byte[] Normal, IReadOnlyDictionary<string, AtlasRect> Rects, int Size, int SizeCap, int Reduced, int Skipped, string Hash);

    /// <summary>
    /// Builds the pair, or returns null when no billboard can be read or the two atlases do not line up. A normal map that is
    /// missing or of another size than its colour texture is replaced by a flat one, because both atlases share one layout.
    /// </summary>
    /// <param name="read">Returns a texture's file, or null when it does not exist.</param>
    /// <param name="maxTile">Largest side a billboard keeps, whatever the atlas size.</param>
    public static Result? Build(IEnumerable<Tile> tiles, Func<string, byte[]?> read, int maxTile, int maxSize = 4096)
    {
        var diffuse = new List<TreeAtlasBuilder.Input>();
        var normal = new List<TreeAtlasBuilder.Input>();
        var flat = new Dictionary<(int, int), byte[]>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tile in tiles)
        {
            RunLimits.Check();
            if (!seen.Add(tile.Key)) continue;
            var d = read(tile.Diffuse);
            if (d is null) continue;
            DdsFile.Info info;
            try { info = DdsFile.ReadInfo(d); }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { continue; }
            var n = tile.Normal is null ? null : read(tile.Normal);
            if (n is not null)
            {
                try { var ni = DdsFile.ReadInfo(n); if (ni.Width != info.Width || ni.Height != info.Height) n = null; }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { n = null; }
            }
            if (n is null && !flat.TryGetValue((info.Width, info.Height), out n)) flat[(info.Width, info.Height)] = n = FlatNormalDds(info.Width, info.Height);
            diffuse.Add(new TreeAtlasBuilder.Input(tile.Key, d));
            normal.Add(new TreeAtlasBuilder.Input(tile.Key, n));
        }
        if (diffuse.Count == 0) return null;

        var builder = new TreeAtlasBuilder { MaxSize = maxSize, MaxTile = maxTile };
        var a = builder.Build(diffuse);
        var b = builder.Build(normal, normalMap: true);
        if (a.Size != b.Size || b.Errors.Count > 0 || a.Rects.Count != b.Rects.Count
            || a.Rects.Any(kv => !b.Rects.TryGetValue(kv.Key, out var r) || r != kv.Value))
            return null;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(a.Dds); hash.AppendData(b.Dds);
        return new Result(a.Dds, b.Dds, new Dictionary<string, AtlasRect>(a.Rects, StringComparer.OrdinalIgnoreCase), a.Size, a.SizeCap, a.Reduced,
            a.Errors.Count, Convert.ToHexString(hash.GetHashAndReset(), 0, 6).ToLowerInvariant());
    }

    /// <summary>Writes the pair into an output folder under the given game paths.</summary>
    public static void Write(Result atlas, string outputFolder, string diffusePath, string normalPath)
    {
        foreach (var (rel, bytes) in new[] { (diffusePath, atlas.Diffuse), (normalPath, atlas.Normal) })
        {
            var full = GamePath.ToFileSystemPath(outputFolder, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
        }
    }

    /// <summary>A BC7 DDS of the given size filled with the flat tangent-space normal (128,128,255), one mip.</summary>
    private static byte[] FlatNormalDds(int width, int height)
    {
        var px = new byte[64];
        for (int i = 0; i < 64; i += 4) { px[i] = 128; px[i + 1] = 128; px[i + 2] = 255; px[i + 3] = 255; }
        var block = new byte[16];
        Bc7Encoder.EncodeBlock(px, block);
        int blocks = ((width + 3) / 4) * ((height + 3) / 4);
        var top = new byte[blocks * 16];
        for (int i = 0; i < top.Length; i += 16) block.CopyTo(top, i);
        using var ms = new MemoryStream();
        DdsFile.WriteBc7(ms, width, height, [top]);
        return ms.ToArray();
    }
}
