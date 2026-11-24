using System.Numerics;
using PenguLOD.Textures.Bc;
using PenguLOD.Textures.Dds;

namespace PenguLOD.Textures;

/// <summary>
/// Converts TruePBR and Complex Material albedo textures into vanilla-shader LOD textures, baking <c>_rmaos.dds</c>
/// (<c>R=roughness, G=metallic, B=AO</c>) and <c>_m.dds</c> masks into diffuse shading and <c>_n.dds</c> specular gloss alpha.
/// </summary>
public static class PbrLodConverter
{
    public const float Gamma = 1.15f;
    public const float BaseScale = 0.65f;
    public const float MetallicDarkening = 0.55f;
    public const float DefaultAoStrength = 0.6f;

    public const byte AlphaThreshold = 128;

    /// <summary>Bump when the mip chain or PBR baking math changes, so converted copies written by an older build are made again.</summary>
    public const int MipVersion = 4; // TruePBR JSON + _rmaos/_m AO/metallic/gloss baking

    public static byte[] Convert(byte[] dds, int maxSize, float brightness,
        byte[]? rmaosDds = null, byte[]? complexMaskDds = null, Vector3? colorScale = null, float aoStrength = DefaultAoStrength)
    {
        var (rgba, w, h, _) = DdsFile.DecodeTopMip(dds);
        while (Math.Max(w, h) > maxSize && Math.Min(w, h) > 4)
            (rgba, w, h) = TreeAtlasBuilder.Downsample(rgba, w, h);
        var aligned = LodTextureEncoding.BlockAligned(new(rgba, w, h), LodTextureEncoding.Channels.Color);
        (rgba, w, h) = (aligned.Pixels, aligned.Width, aligned.Height);

        BakeMaterialMaps(rgba, w, h, rmaosDds, complexMaskDds, colorScale, aoStrength);
        ApplyCurve(rgba, brightness);

        var mips = MipChain(rgba, w, h).Select(m => Bc7Encoder.EncodeImage(m.Rgba, m.W, m.H)).ToList();
        using var ms = new MemoryStream();
        DdsFile.WriteBc7(ms, w, h, mips);
        return ms.ToArray();
    }

    /// <summary>Applies <c>_rmaos.dds</c> (<c>G=metallic, B=AO</c>), <c>_m.dds</c> fallback, and <paramref name="colorScale"/> in place before the LOD curve.</summary>
    public static void BakeMaterialMaps(byte[] rgba, int w, int h, byte[]? rmaosDds, byte[]? complexMaskDds,
        Vector3? colorScale = null, float aoStrength = DefaultAoStrength)
    {
        Vector3 tint = colorScale ?? Vector3.One;
        bool hasTint = Vector3.DistanceSquared(tint, Vector3.One) > 1e-6f;
        TextureImage? rmaos = DecodeOptional(rmaosDds, w, h);
        TextureImage? mask = rmaos is null ? DecodeOptional(complexMaskDds, w, h) : null;
        if (!hasTint && rmaos is null && mask is null) return;

        for (int i = 0; i < rgba.Length; i += 4)
        {
            float metal = 0f, ao = 1f;
            if (rmaos is not null)
            {
                metal = rmaos.Pixels[i + 1] / 255f;
                ao = rmaos.Pixels[i + 2] / 255f;
            }
            else if (mask is not null)
            {
                metal = mask.Pixels[i + 2] / 255f;
                ao = mask.Pixels[i] > 0 ? Math.Max(0.35f, mask.Pixels[i] / 255f) : 1f;
            }
            float response = (1f - metal * MetallicDarkening) * (1f - aoStrength * (1f - ao));
            rgba[i] = (byte)Math.Clamp(MathF.Round(rgba[i] * tint.X * response), 0, 255);
            rgba[i + 1] = (byte)Math.Clamp(MathF.Round(rgba[i + 1] * tint.Y * response), 0, 255);
            rgba[i + 2] = (byte)Math.Clamp(MathF.Round(rgba[i + 2] * tint.Z * response), 0, 255);
        }
    }

    /// <summary>Converts a PBR/CM normal map into a LOD <c>_n.dds</c> with <c>(1 - roughness)</c> specular gloss encoded in Alpha.</summary>
    public static byte[] ConvertNormalWithGloss(byte[] normalDds, int maxSize, byte[]? rmaosDds, byte[]? complexMaskDds,
        float roughnessScale = 1f, float roughnessOffset = 0f)
    {
        var (rgba, w, h, _) = DdsFile.DecodeTopMip(normalDds);
        for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255; // prevent normal downsampling from alpha-weighting RGB
        while (Math.Max(w, h) > maxSize && Math.Min(w, h) > 4)
            (rgba, w, h) = TreeAtlasBuilder.Downsample(rgba, w, h, alphaWeighted: false);
        var aligned = LodTextureEncoding.BlockAligned(new(rgba, w, h), LodTextureEncoding.Channels.Normal);
        (rgba, w, h) = (aligned.Pixels, aligned.Width, aligned.Height);

        TextureImage? rmaos = DecodeOptional(rmaosDds, w, h);
        TextureImage? mask = rmaos is null ? DecodeOptional(complexMaskDds, w, h) : null;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            TextureImages.Normalize(rgba, i);
            float gloss = 0.25f;
            if (rmaos is not null)
            {
                float rough = Math.Clamp((rmaos.Pixels[i] / 255f) * roughnessScale + roughnessOffset, 0f, 1f);
                gloss = 1f - rough;
            }
            else if (mask is not null)
            {
                gloss = Math.Clamp(mask.Pixels[i + 1] / 255f, 0f, 1f);
            }
            rgba[i + 3] = (byte)Math.Clamp(MathF.Round(gloss * 255f), 0, 255);
        }

        return LodTextureEncoding.Encode(new(rgba, w, h), LodTextureEncoding.Channels.Normal);
    }

    private static TextureImage? DecodeOptional(byte[]? dds, int targetW, int targetH)
    {
        if (dds is null) return null;
        try
        {
            var decoded = LodTextureEncoding.Decode(dds);
            return decoded.Width == targetW && decoded.Height == targetH
                ? decoded
                : LodTextureEncoding.Resample(decoded, targetW, targetH, LodTextureEncoding.Channels.Data);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    public static void ApplyCurve(byte[] rgba, float brightness)
    {
        var lut = new byte[256];
        for (int i = 0; i < 256; i++)
            lut[i] = (byte)Math.Clamp(MathF.Round(255f * MathF.Pow(i / 255f, Gamma) * BaseScale * brightness), 0, 255);
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = lut[rgba[i]];
            rgba[i + 1] = lut[rgba[i + 1]];
            rgba[i + 2] = lut[rgba[i + 2]];
        }
    }

    public static List<(byte[] Rgba, int W, int H)> MipChain(byte[] rgba, int w, int h)
    {
        var levels = new List<(byte[] Rgba, int W, int H)> { (rgba, w, h) };
        float target = Coverage(rgba, 1f);
        var level = rgba;
        int mw = w, mh = h;
        while (mw > 4 || mh > 4)
        {
            (level, mw, mh) = TreeAtlasBuilder.Downsample(level, mw, mh);
            var shown = level;
            if (target is > 0f and < 0.999f)
            {
                shown = (byte[])level.Clone();
                KeepCoverage(shown, target);
            }
            levels.Add((shown, mw, mh));
        }
        return levels;
    }

    internal static float Coverage(byte[] rgba, float scale, int threshold = AlphaThreshold, bool quantized = false)
    {
        int count = 0, total = rgba.Length / 4;
        for (int i = 3; i < rgba.Length; i += 4)
            if ((quantized ? MathF.Round(rgba[i] * scale) : rgba[i] * scale) >= threshold) count++;
        return total == 0 ? 0f : (float)count / total;
    }

    internal static void KeepCoverage(byte[] rgba, float target, int threshold = AlphaThreshold, bool preserveMatching = false)
    {
        if (preserveMatching && MathF.Abs(Coverage(rgba, 1, threshold) - target) <= .5f / Math.Max(1, rgba.Length / 4)) return;
        float lo = 0.25f, hi = 8f;
        for (int i = 0; i < 12; i++)
        {
            float mid = (lo + hi) / 2;
            if (Coverage(rgba, mid, threshold, quantized: preserveMatching) < target) lo = mid; else hi = mid;
        }
        if (preserveMatching && MathF.Abs(Coverage(rgba, hi, threshold, quantized: true) - target)
            >= MathF.Abs(Coverage(rgba, 1, threshold) - target)) return;
        if (MathF.Abs(hi - 1f) < 0.01f) return;
        for (int i = 3; i < rgba.Length; i += 4)
            rgba[i] = (byte)Math.Min(255, MathF.Round(rgba[i] * hi));
    }
}
