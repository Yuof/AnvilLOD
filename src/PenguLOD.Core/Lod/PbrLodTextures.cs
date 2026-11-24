using System.Collections.Concurrent;
using PenguLOD.Core.Pipeline;

namespace PenguLOD.Core.Lod;

/// <summary>
/// Makes object LOD use textures that match TruePBR and Complex Material full models, baking <c>_rmaos.dds</c> / <c>_m.dds</c>
/// ambient occlusion, metallic diffuse response, and specular gloss into converted diffuse and <c>_n.dds</c> LOD pairs.
/// </summary>
public sealed class PbrLodTextures(IAssetSource assets, TruePbrCatalog? catalog = null)
{
    public const string OutputRoot = "textures\\pengulod\\pbr\\";

    private readonly ConcurrentDictionary<string, string?> _diffuse = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _conversions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string NormalSource, string? RmaosSource, string? ComplexMaskSource)> _normalConversions = new(StringComparer.OrdinalIgnoreCase);
    private int _twins;

    public TruePbrCatalog? Catalog { get; } = catalog;

    /// <summary>Converted diffuse copies to write: output path (relative to LOD output) → PBR/CM albedo path.</summary>
    public IReadOnlyDictionary<string, string> Conversions => _conversions;

    /// <summary>Converted normal+gloss copies to write: output path → (source normal, rmaos, complex mask).</summary>
    public IReadOnlyDictionary<string, (string NormalSource, string? RmaosSource, string? ComplexMaskSource)> NormalConversions => _normalConversions;

    public int TwinsUsed => _twins;

    public static string Normalize(string path)
    {
        var p = path.Replace('/', '\\').Trim().TrimStart('\\');
        if (p.StartsWith("data\\", StringComparison.OrdinalIgnoreCase)) p = p[5..];
        return p.ToLowerInvariant();
    }

    public static (string? Rmaos, string? ComplexMask) FindCompanionMaps(IAssetSource assets, string albedoPath)
    {
        var p = Normalize(albedoPath);
        if (!p.EndsWith(".dds", StringComparison.Ordinal)) return (null, null);
        string stem = p[..^4];
        string rmaos = stem + "_rmaos.dds";
        string mask = stem + "_m.dds";
        return (assets.Exists(rmaos) ? rmaos : null, assets.Exists(mask) ? mask : null);
    }

    public string? MapDiffuse(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return _diffuse.GetOrAdd(path, Compute);
    }

    public string? MapNormal(string diffuse, string normal)
    {
        var mapped = MapDiffuse(diffuse);
        if (mapped is null) return null;
        if (!mapped.StartsWith(OutputRoot, StringComparison.OrdinalIgnoreCase))
        {
            var twinNormal = mapped[..^4] + "_n.dds";
            return assets.Exists(twinNormal) ? twinNormal : null;
        }
        if (!_conversions.TryGetValue(mapped, out var pbrAlbedo)) return null;
        var (rmaos, mask) = FindCompanionMaps(assets, pbrAlbedo);
        if (rmaos is null && mask is null) return null;

        string candidateNormal = pbrAlbedo[..^4] + "_n.dds";
        if (!assets.Exists(candidateNormal) && !string.IsNullOrWhiteSpace(normal))
        {
            string normClean = Normalize(normal);
            if (assets.Exists(normClean)) candidateNormal = normClean;
        }
        if (!assets.Exists(candidateNormal)) return null;

        string outNormal = mapped[..^4] + "_n.dds";
        _normalConversions[outNormal] = (candidateNormal, rmaos, mask);
        return outNormal;
    }

    private string? Compute(string original)
    {
        var p = Normalize(original);
        if (!p.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) || !p.StartsWith("textures\\", StringComparison.OrdinalIgnoreCase)) return null;
        if (p.StartsWith("textures\\pengulod\\", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith("pbr_lod.dds", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("textures\\terrain\\", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("textures\\dyndolod\\", StringComparison.OrdinalIgnoreCase)) return null;

        const string pbrRoot = "textures\\pbr\\";
        if (p.StartsWith(pbrRoot, StringComparison.OrdinalIgnoreCase))
        {
            if (p.EndsWith("lod.dds", StringComparison.OrdinalIgnoreCase) || p.Contains("\\lod\\", StringComparison.Ordinal) || !assets.Exists(p)) return null;
            var converted = OutputRoot + p[pbrRoot.Length..];
            _conversions[converted] = p;
            return converted;
        }

        if (p.EndsWith("lod.dds", StringComparison.OrdinalIgnoreCase))
        {
            var twin = p[..^"lod.dds".Length] + "pbr_lod.dds";
            if (assets.Exists(twin)) { Interlocked.Increment(ref _twins); return twin; }
        }

        var pbr = "textures\\pbr\\" + p["textures\\".Length..];
        if (assets.Exists(pbr))
        {
            var output = OutputRoot + p["textures\\".Length..];
            _conversions[output] = pbr;
            return output;
        }

        // Complex Material fallback: non-LOD texture with an explicit TruePBR JSON rule and companion _rmaos.dds or _m.dds
        if (!p.EndsWith("lod.dds", StringComparison.Ordinal) && !p.Contains("\\lod\\", StringComparison.Ordinal)
            && Catalog?.Match(p) is not null && assets.Exists(p))
        {
            var (rmaos, mask) = FindCompanionMaps(assets, p);
            if (rmaos is not null || mask is not null)
            {
                var output = OutputRoot + p["textures\\".Length..];
                _conversions[output] = p;
                return output;
            }
        }
        return null;
    }
}
