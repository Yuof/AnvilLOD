using System.Numerics;
using System.Runtime.CompilerServices;
using PenguLOD.Core.Pipeline;
using PenguLOD.Meshes;
using PenguLOD.Textures;
using PenguLOD.Textures.Bc;
using PenguLOD.Textures.Dds;

namespace PenguLOD.Plugins.Authoring;

public sealed record BillboardImage(byte[] Rgba, int Width, int Height, float WorldWidth, float WorldHeight, float ShiftZ, float Coverage, byte[]? NormalRgba = null);

/// <summary>
/// Mipmapped orthographic software rasterizer for tree/grass billboards and TexGen <c>RenderedObjects</c>:
/// analytical per-triangle screen-space UV footprint mip selection, normal-map gloss preservation,
/// vertex-alpha overlay layer blending, and emissive glow (<c>_g.dds</c>) baking.
/// </summary>
public static class BillboardRenderer
{
    public delegate (byte[] Rgba, int Width, int Height)? TextureLoader(string texturePath);

    private sealed class MipPyramid
    {
        public readonly List<(byte[] Rgba, int Width, int Height)> Levels = [];
        public MipPyramid((byte[] Rgba, int Width, int Height) top, bool alphaWeighted)
        {
            Levels.Add(top);
            var cur = top;
            while (cur.Width > 1 || cur.Height > 1)
            {
                cur = TreeAtlasBuilder.Downsample(cur.Rgba, cur.Width, cur.Height, alphaWeighted);
                Levels.Add(cur);
            }
        }
    }

    private static readonly ConditionalWeakTable<byte[], MipPyramid> ColorPyramids = new();
    private static readonly ConditionalWeakTable<byte[], MipPyramid> DataPyramids = new();

    public static BillboardImage? Render(LodMesh model, TextureLoader loadTexture, int maxPixels = 512, bool vertexColors = true, float minCoverage = 0f, float unitsPerPixel = 0, (Vector3 Min, Vector3 Max)? viewport = null, float brightness = 1, bool shaded = true, (int W, int H)? pixelSize = null, MaterialBakeSettings? materials = null,
        BillboardView view = BillboardView.Front, bool originCentered = false, GrassTextureMode grassTextures = GrassTextureMode.Basic, float complexGrassThreshold = .1f,
        BillboardLight? light = null)
    {
        light ??= BillboardLight.Tree;
        light.Validate();
        if (model.HasIncompleteGeometry) throw new NotSupportedException("Cannot render incomplete geometry: " + string.Join("; ", model.Warnings));
        if (view == BillboardView.Side)
        {
            static Vector3 Project(Vector3 p) => new(p.Y, -p.X, p.Z);
            model = new LodMesh { Path = model.Path, Parts = model.Parts.Select(p => p with
            {
                Positions = p.Positions.Select(Project).ToArray(), Normals = p.Normals.Select(Project).ToArray(),
                Tangents = p.Tangents.Select(Project).ToArray(), Bitangents = p.Bitangents.Select(Project).ToArray(),
            }).ToArray() };
        }
        materials ??= new();
        materials.Validate();
        var (min, max) = viewport ?? Meshes.Authoring.LodMeshAuthor.Bounds(model);
        if (originCentered && viewport is null)
        {
            float half = MathF.Max(MathF.Abs(min.X), MathF.Abs(max.X));
            min.X = -half; max.X = half;
        }
        float worldW = max.X - min.X, worldH = max.Z - min.Z;
        if (!(worldW > 1f) || !(worldH > 1f)) return null;

        float scale = maxPixels / MathF.Max(worldW, worldH);
        int outW = Math.Max(16, Align4((int)MathF.Ceiling(worldW * scale)));
        int outH = Math.Max(16, Align4((int)MathF.Ceiling(worldH * scale)));
        if (unitsPerPixel > 0)
        {
            outW = PowerOfTwo(worldW / unitsPerPixel, maxPixels);
            outH = PowerOfTwo(worldH / unitsPerPixel, maxPixels);
        }
        if (pixelSize is { } size) { outW = size.W; outH = size.H; }
        if (outW is < 4 or > 4096 || outH is < 4 or > 4096) throw new ArgumentOutOfRangeException(nameof(pixelSize));
        int ss = 2, w = outW * ss, h = outH * ss;
        float sx = w / worldW, sz = h / worldH;

        var color = new byte[w * h * 4];
        var normalMap = new byte[w * h * 4];
        var glossMap = !shaded ? new byte[w * h] : null;
        if (glossMap is not null) Array.Fill(glossMap, (byte)255);
        var depth = new float[w * h];
        Array.Fill(depth, float.MaxValue);
        int[]? blendStamp = null;

        // Render opaque/cutout parts first, then alpha-blended decal/layer parts so overlays composite cleanly.
        var orderedParts = model.Parts.OrderBy(pt => pt.Material.HasAlpha && (pt.Material.AlphaFlags & 1) != 0 && (pt.Material.AlphaFlags & 0x200) == 0 ? 1 : 0).ToArray();
        for (int partIdx = 0; partIdx < orderedParts.Length; partIdx++)
        {
            var part = orderedParts[partIdx];
            RunLimits.Check();
            if (part.Material.Textures.Count == 0 || string.IsNullOrWhiteSpace(part.Material.Textures[0]))
                throw new InvalidDataException("Render shape has no diffuse texture: " + part.Name);
            var tex = loadTexture(part.Material.Textures[0]) ?? throw new FileNotFoundException("Diffuse texture missing: " + part.Material.Textures[0]);
            var effect = part.Material.RenderEffect;
            var normalTexture = materials.SourceNormals && part.Material.Textures.Count > 1 ? Optional(part.Material.Textures[1]) : null;
            if (grassTextures != GrassTextureMode.Basic)
            {
                var decoded = GrassTexture.Decode(tex, grassTextures, complexGrassThreshold);
                tex = decoded.Diffuse;
                if (materials.SourceNormals && decoded.Normal is { } packedNormal) normalTexture = packedNormal;
            }
            bool pbr = materials.PbrAlbedo && part.Material.Textures[0].Replace('/', '\\').Contains(@"\pbr\", StringComparison.OrdinalIgnoreCase);
            var occlusion = pbr && materials.OcclusionStrength > 0 && part.Material.Textures.Count > 5 ? Optional(part.Material.Textures[5]) : null;
            bool hasEmissive = part.Material.EmissiveMultiple > 0 && part.Material.EmissiveColor.LengthSquared() > 1e-6f
                && ((part.Material.ShaderFlags1 & (1u << 22)) != 0 || (part.Material.Textures.Count > 2 && !string.IsNullOrWhiteSpace(part.Material.Textures[2])));
            var glowTexture = hasEmissive && part.Material.Textures.Count > 2 ? Optional(part.Material.Textures[2]) : null;

            var texMips = GetPyramid(tex, alphaWeighted: part.Material.HasAlpha);
            var normMips = normalTexture is { } nt ? GetPyramid(nt, alphaWeighted: false) : null;
            var occMips = occlusion is { } ot ? GetPyramid(ot, alphaWeighted: false) : null;
            var glowMips = glowTexture is { } gt ? GetPyramid(gt, alphaWeighted: false) : null;

            bool alphaBlend = effect is null && part.Material.HasAlpha && (part.Material.AlphaFlags & 1) != 0 && (part.Material.AlphaFlags & 0x200) == 0;
            byte threshold = part.Material.HasAlpha && !alphaBlend ? (part.Material.AlphaThreshold == 0 ? (byte)128 : part.Material.AlphaThreshold) : (byte)0;
            Vector2 uvScale = part.Material.Passthru is { } sh ? new Vector2(sh.UvScaleX, sh.UvScaleY) : Vector2.One;
            Vector2 uvOffset = part.Material.Passthru is { } shOff ? new Vector2(shOff.UvOffsetX, shOff.UvOffsetY) : Vector2.Zero;
            var p = part.Positions;

            for (int t = 0; t < part.TriangleCount; t++)
            {
                RunLimits.Check();
                int i0 = part.Triangles[t * 3], i1 = part.Triangles[t * 3 + 1], i2 = part.Triangles[t * 3 + 2];
                var a = Screen(p[i0]); var b = Screen(p[i1]); var c = Screen(p[i2]);
                float area = Edge(a, b, c.X, c.Y);
                if (MathF.Abs(area) < 1e-6f) continue;

                // Analytical orthographic screen-space UV footprint -> constant mip level across the triangle!
                float invArea = 1f / area;
                float dw0x = (b.Y - c.Y) * invArea, dw1x = (c.Y - a.Y) * invArea, dw2x = -dw0x - dw1x;
                float dw0y = (c.X - b.X) * invArea, dw1y = (a.X - c.X) * invArea, dw2y = -dw0y - dw1y;
                var uv0 = part.UVs[i0] * uvScale + uvOffset;
                var uv1 = part.UVs[i1] * uvScale + uvOffset;
                var uv2 = part.UVs[i2] * uvScale + uvOffset;
                Vector2 dudx = (uv0 * dw0x + uv1 * dw1x + uv2 * dw2x) * new Vector2(tex.Width, tex.Height);
                Vector2 dudy = (uv0 * dw0y + uv1 * dw1y + uv2 * dw2y) * new Vector2(tex.Width, tex.Height);
                float maxRhoSq = MathF.Max(dudx.LengthSquared(), dudy.LengthSquared());
                int mipLod = maxRhoSq > 1f && float.IsFinite(maxRhoSq) ? Math.Max(0, (int)MathF.Floor(0.5f * MathF.Log2(maxRhoSq))) : 0;
                var activeTex = texMips.Levels[Math.Min(mipLod, texMips.Levels.Count - 1)];
                var activeNorm = normMips is null ? ((byte[], int, int)?)null : normMips.Levels[Math.Min(mipLod, normMips.Levels.Count - 1)];
                var activeOcc = occMips is null ? ((byte[], int, int)?)null : occMips.Levels[Math.Min(mipLod, occMips.Levels.Count - 1)];
                var activeGlow = glowMips is null ? ((byte[], int, int)?)null : glowMips.Levels[Math.Min(mipLod, glowMips.Levels.Count - 1)];

                int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
                int x1 = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
                int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
                int y1 = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
                for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    if (x == x0) RunLimits.Check();
                    float px = x + 0.5f, py = y + 0.5f;
                    float w0 = Edge(b, c, px, py) * invArea, w1 = Edge(c, a, px, py) * invArea, w2 = 1f - w0 - w1;
                    if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                    float z = w0 * a.Z + w1 * b.Z + w2 * c.Z;
                    int idx = y * w + x;
                    if (alphaBlend ? z > depth[idx] + 0.5f : z >= depth[idx]) continue;

                    var uv = uv0 * w0 + uv1 * w1 + uv2 * w2;
                    var (r, g, bl, al) = Sample(activeTex, uv, part.Material.ClampMode);
                    al *= (part.Material.Passthru?.Alpha ?? 1) * (effect?.Tint.W ?? 1);
                    if ((alphaBlend || (effect is not null && (part.Material.ShaderFlags1 & 8u) != 0)) && part.Colors is { } alphaColors)
                        al *= ((alphaColors[i0] >> 24) * w0 + (alphaColors[i1] >> 24) * w1 + (alphaColors[i2] >> 24) * w2) / 255f;
                    if (effect is not null)
                    {
                        if (part.Material.HasAlpha && (part.Material.AlphaFlags & 0x200) != 0
                            && !AlphaPasses(al, part.Material.AlphaThreshold, (part.Material.AlphaFlags >> 10) & 7)) continue;
                    }
                    else if (alphaBlend)
                    {
                        if (al <= 1f) continue;
                    }
                    else if (part.Material.HasAlpha && (al < threshold || al <= 0)) continue;

                    if (effect is not null) { r *= effect.Tint.X; g *= effect.Tint.Y; bl *= effect.Tint.Z; }
                    if (pbr)
                    {
                        float ao = activeOcc is null ? 1 : Sample(activeOcc, uv, part.Material.ClampMode).B / 255f;
                        float factor = materials.PbrScale * (1 - materials.OcclusionStrength * (1 - ao));
                        r = 255 * MathF.Pow(r / 255, materials.PbrGamma) * factor;
                        g = 255 * MathF.Pow(g / 255, materials.PbrGamma) * factor;
                        bl = 255 * MathF.Pow(bl / 255, materials.PbrGamma) * factor;
                    }
                    if ((effect is null ? vertexColors : (part.Material.ShaderFlags2 & 0x20u) != 0) && part.Colors is { } col)
                    {
                        var vc = Unpack(col[i0]) * w0 + Unpack(col[i1]) * w1 + Unpack(col[i2]) * w2;
                        r *= vc.X; g *= vc.Y; bl *= vc.Z;
                    }
                    var n = Vector3.Normalize(part.Normals[i0] * w0 + part.Normals[i1] * w1 + part.Normals[i2] * w2);
                    if (!float.IsFinite(n.X)) n = -Vector3.UnitY;
                    float gloss = 255f;
                    if (activeNorm is not null)
                    {
                        var ns = Sample(activeNorm, uv, part.Material.ClampMode);
                        gloss = ns.A;
                        var alongU = part.Bitangents[i0] * w0 + part.Bitangents[i1] * w1 + part.Bitangents[i2] * w2;
                        alongU -= n * Vector3.Dot(alongU, n);
                        if (alongU.LengthSquared() > 1e-8f)
                        {
                            alongU = Vector3.Normalize(alongU);
                            var rawV = part.Tangents[i0] * w0 + part.Tangents[i1] * w1 + part.Tangents[i2] * w2;
                            var alongV = Vector3.Cross(n, alongU);
                            if (Vector3.Dot(alongV, rawV) < 0) alongV = -alongV;
                            var mapped = alongU * ((ns.R - 128) / 127) + alongV * ((ns.G - 128) / 127) + n * ((ns.B - 128) / 127);
                            if (mapped.LengthSquared() > 1e-8f) n = Vector3.Normalize(mapped);
                        }
                    }
                    float shade = brightness * (shaded ? light.Shade(n) : 1);
                    if (effect is not null) shade = brightness + (shade - brightness) * effect.LightingInfluence;
                    float outR = r * shade, outG = g * shade, outB = bl * shade;
                    if (hasEmissive)
                    {
                        var em = activeGlow is not null ? Sample(activeGlow, uv, part.Material.ClampMode) : (R: 255f, G: 255f, B: 255f, A: 255f);
                        outR += em.R * part.Material.EmissiveColor.X * part.Material.EmissiveMultiple;
                        outG += em.G * part.Material.EmissiveColor.Y * part.Material.EmissiveMultiple;
                        outB += em.B * part.Material.EmissiveColor.Z * part.Material.EmissiveMultiple;
                    }

                    int o = idx * 4;
                    var nv = float.IsFinite(n.X) ? n : -Vector3.UnitY;
                    if (nv.Y > 0) nv = -nv;
                    byte encNx = (byte)Math.Clamp(MathF.Round(128f + 127f * nv.X), 0, 255);
                    byte encNy = (byte)Math.Clamp(MathF.Round(128f - 127f * nv.Z), 0, 255);
                    byte encNz = (byte)Math.Clamp(MathF.Round(128f - 127f * nv.Y), 0, 255);

                    if (alphaBlend && color[o + 3] > 0)
                    {
                        if (blendStamp is null) { blendStamp = new int[w * h]; Array.Fill(blendStamp, -1); }
                        if (blendStamp[idx] == partIdx) continue;
                        blendStamp[idx] = partIdx;
                        float blend = Math.Clamp(al / 255f, 0f, 1f);
                        color[o] = (byte)Math.Clamp(MathF.Round(color[o] * (1f - blend) + outR * blend), 0, 255);
                        color[o + 1] = (byte)Math.Clamp(MathF.Round(color[o + 1] * (1f - blend) + outG * blend), 0, 255);
                        color[o + 2] = (byte)Math.Clamp(MathF.Round(color[o + 2] * (1f - blend) + outB * blend), 0, 255);
                        normalMap[o] = (byte)Math.Clamp(MathF.Round(normalMap[o] * (1f - blend) + encNx * blend), 0, 255);
                        normalMap[o + 1] = (byte)Math.Clamp(MathF.Round(normalMap[o + 1] * (1f - blend) + encNy * blend), 0, 255);
                        normalMap[o + 2] = (byte)Math.Clamp(MathF.Round(normalMap[o + 2] * (1f - blend) + encNz * blend), 0, 255);
                        if (glossMap is not null)
                            glossMap[idx] = (byte)Math.Clamp(MathF.Round(glossMap[idx] * (1f - blend) + gloss * blend), 0, 255);
                    }
                    else
                    {
                        depth[idx] = z;
                        normalMap[o] = encNx;
                        normalMap[o + 1] = encNy;
                        normalMap[o + 2] = encNz;
                        normalMap[o + 3] = 255;
                        if (glossMap is not null) glossMap[idx] = (byte)Math.Clamp(MathF.Round(gloss), 0, 255);
                        color[o] = (byte)Math.Clamp(outR, 0, 255);
                        color[o + 1] = (byte)Math.Clamp(outG, 0, 255);
                        color[o + 2] = (byte)Math.Clamp(outB, 0, 255);
                        color[o + 3] = 255;
                    }
                }
            }
        }

        var (img, iw, ih) = TreeAtlasBuilder.Downsample(color, w, h);
        for (int i = 3; i < img.Length; i += 4) img[i] = img[i] >= 96 ? (byte)255 : (byte)0;
        for (int pass = 0; pass < 6 && minCoverage > 0 && Coverage(img, 1f) < minCoverage; pass++) Thicken(img, iw, ih);
        Bleed(img, iw, ih, 6);
        var (nimg, _, _) = TreeAtlasBuilder.Downsample(normalMap, w, h);
        for (int i = 0; i < nimg.Length; i += 4) nimg[i + 3] = img[i + 3];
        Bleed(nimg, iw, ih, 6);
        for (int py = 0; py < ih; py++)
            for (int px = 0; px < iw; px++)
            {
                int i = (py * iw + px) * 4;
                if (nimg[i] == 0 && nimg[i + 1] == 0 && nimg[i + 2] == 0) { nimg[i] = 128; nimg[i + 1] = 128; nimg[i + 2] = 255; }
                Renormalize(nimg, i);
                if (glossMap is not null)
                {
                    int gSum = glossMap[(py * 2) * w + (px * 2)] + glossMap[(py * 2) * w + (px * 2 + 1)]
                             + glossMap[(py * 2 + 1) * w + (px * 2)] + glossMap[(py * 2 + 1) * w + (px * 2 + 1)];
                    nimg[i + 3] = (byte)((gSum + 2) >> 2);
                }
                else
                {
                    nimg[i + 3] = 255;
                }
            }
        int opaque = 0;
        for (int i = 3; i < img.Length; i += 4) if (img[i] >= 128) opaque++;
        if (opaque == 0) return null;
        return new BillboardImage(img, iw, ih, worldW, worldH, min.Z, opaque / (float)(iw * ih), nimg);

        Vector3 Screen(Vector3 v) => new((v.X - min.X) * sx, (max.Z - v.Z) * sz, v.Y);
        (byte[] Rgba, int Width, int Height)? Optional(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return loadTexture(path); }
            catch (FileNotFoundException) { return null; }
        }
    }

    private static MipPyramid GetPyramid((byte[] Rgba, int Width, int Height) tex, bool alphaWeighted)
        => (alphaWeighted ? ColorPyramids : DataPyramids).GetValue(tex.Rgba, _ => new MipPyramid(tex, alphaWeighted));

    /// <summary>Writes the image as a BC7 DDS with coverage-preserving mips, downsampling each level from unboosted alpha.</summary>
    public static void WriteDds(Stream output, byte[] rgba, int width, int height)
    {
        float coverage = Coverage(rgba, 1f);
        var mips = new List<byte[]> { Bc7Encoder.EncodeImage(rgba, width, height) };
        var (cur, w, h) = (rgba, width, height);
        while (w > 4 || h > 4)
        {
            (cur, w, h) = TreeAtlasBuilder.Downsample(cur, w, h);
            float lo = 0.5f, hi = 4f;
            for (int i = 0; i < 12; i++)
            {
                float mid = (lo + hi) / 2;
                if (Coverage(cur, mid) < coverage) lo = mid; else hi = mid;
            }
            float s = (lo + hi) / 2;
            var shown = (byte[])cur.Clone();
            for (int i = 3; i < shown.Length; i += 4) shown[i] = (byte)Math.Min(255f, shown[i] * s);
            mips.Add(Bc7Encoder.EncodeImage(shown, w, h));
        }
        DdsFile.WriteBc7(output, width, height, mips);
    }

    public static void WriteNormalDds(Stream output, byte[] rgba, int width, int height)
    {
        var mips = new List<byte[]> { Bc7Encoder.EncodeImage(rgba, width, height) };
        var (cur, w, h) = (rgba, width, height);
        while (w > 4 || h > 4)
        {
            (cur, w, h) = TreeAtlasBuilder.Downsample(cur, w, h, alphaWeighted: false);
            for (int i = 0; i < cur.Length; i += 4) { Renormalize(cur, i); cur[i + 3] = 255; }
            mips.Add(Bc7Encoder.EncodeImage(cur, w, h));
        }
        DdsFile.WriteBc7(output, width, height, mips);
    }

    internal static void Renormalize(byte[] px, int i)
    {
        float x = (px[i] - 128f) / 127f, y = (px[i + 1] - 128f) / 127f, z = (px[i + 2] - 128f) / 127f;
        float len = MathF.Sqrt(x * x + y * y + z * z);
        if (!(len > 1e-3f)) { px[i] = 128; px[i + 1] = 128; px[i + 2] = 255; return; }
        if (z < 0.05f) z = 0.05f;
        len = MathF.Sqrt(x * x + y * y + z * z);
        px[i] = (byte)Math.Clamp(MathF.Round(128f + 127f * x / len), 0, 255);
        px[i + 1] = (byte)Math.Clamp(MathF.Round(128f + 127f * y / len), 0, 255);
        px[i + 2] = (byte)Math.Clamp(MathF.Round(128f + 127f * z / len), 0, 255);
    }

    public static void WriteFlatNormalDds(Stream output, int width, int height)
    {
        var px = new byte[width * height * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = 128; px[i + 1] = 128; px[i + 2] = 255; px[i + 3] = 255; }
        var mips = new List<byte[]>();
        var (cur, w, h) = (px, width, height);
        mips.Add(Bc7Encoder.EncodeImage(cur, w, h));
        while (w > 4 || h > 4)
        {
            (cur, w, h) = TreeAtlasBuilder.Downsample(cur, w, h);
            mips.Add(Bc7Encoder.EncodeImage(cur, w, h));
        }
        DdsFile.WriteBc7(output, width, height, mips);
    }

    private static float Coverage(byte[] rgba, float scale)
    {
        int n = 0, total = rgba.Length / 4;
        for (int i = 3; i < rgba.Length; i += 4) if (rgba[i] * scale >= 128f) n++;
        return n / (float)total;
    }

    private static void Thicken(byte[] img, int w, int h)
    {
        var src = (byte[])img.Clone();
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            if (src[i + 3] >= 128) continue;
            int r = 0, g = 0, b = 0, n = 0;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                int j = (ny * w + nx) * 4;
                if (src[j + 3] < 128) continue;
                r += src[j]; g += src[j + 1]; b += src[j + 2]; n++;
            }
            if (n == 0) continue;
            img[i] = (byte)(r / n); img[i + 1] = (byte)(g / n); img[i + 2] = (byte)(b / n); img[i + 3] = 255;
        }
    }

    private static void Bleed(byte[] img, int w, int h, int passes)
    {
        var filled = new bool[w * h];
        for (int i = 0; i < filled.Length; i++) filled[i] = img[i * 4 + 3] > 0;
        for (int pass = 0; pass < passes; pass++)
        {
            var next = (bool[])filled.Clone();
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (filled[i]) continue;
                int r = 0, g = 0, b = 0, n = 0;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h || !filled[ny * w + nx]) continue;
                    int j = (ny * w + nx) * 4;
                    r += img[j]; g += img[j + 1]; b += img[j + 2]; n++;
                }
                if (n == 0) continue;
                img[i * 4] = (byte)(r / n); img[i * 4 + 1] = (byte)(g / n); img[i * 4 + 2] = (byte)(b / n);
                next[i] = true;
            }
            filled = next;
        }
    }

    private static float Edge(Vector3 a, Vector3 b, float px, float py) => (b.X - a.X) * (py - a.Y) - (b.Y - a.Y) * (px - a.X);

    private static bool AlphaPasses(float alpha, byte threshold, int function) => function switch
    {
        0 => true, 1 => alpha < threshold, 2 => alpha == threshold, 3 => alpha <= threshold,
        4 => alpha > threshold, 5 => alpha != threshold, 6 => alpha >= threshold, _ => false
    };

    private static Vector3 Unpack(uint c) => new((c & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, ((c >> 16) & 0xFF) / 255f);

    private static (float R, float G, float B, float A) Sample((byte[] Rgba, int Width, int Height)? tex, Vector2 uv, uint clamp = 3)
    {
        if (tex is not { } t) return (128, 128, 128, 255);
        bool wrapU = (clamp & 2) != 0, wrapV = (clamp & 1) != 0;
        float u = wrapU ? uv.X - MathF.Floor(uv.X) : Math.Clamp(uv.X, 0, 1);
        float v = wrapV ? uv.Y - MathF.Floor(uv.Y) : Math.Clamp(uv.Y, 0, 1);
        float fx = u * t.Width - 0.5f, fy = v * t.Height - 0.5f;
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        float ax = fx - x0, ay = fy - y0;
        float r = 0, g = 0, b = 0, a = 0;
        for (int k = 0; k < 4; k++)
        {
            int dx = k & 1, dy = k >> 1;
            float wgt = (dx == 0 ? 1 - ax : ax) * (dy == 0 ? 1 - ay : ay);
            int px = wrapU ? ((x0 + dx) % t.Width + t.Width) % t.Width : Math.Clamp(x0 + dx, 0, t.Width - 1);
            int py = wrapV ? ((y0 + dy) % t.Height + t.Height) % t.Height : Math.Clamp(y0 + dy, 0, t.Height - 1);
            int i = (py * t.Width + px) * 4;
            r += t.Rgba[i] * wgt; g += t.Rgba[i + 1] * wgt; b += t.Rgba[i + 2] * wgt; a += t.Rgba[i + 3] * wgt;
        }
        return (r, g, b, a);
    }

    private static int PowerOfTwo(float value, int max)
    {
        int size = 16;
        while (size < value && size < max) size *= 2;
        return Math.Min(size, max);
    }

    private static int Align4(int v) => (v + 3) & ~3;
}
