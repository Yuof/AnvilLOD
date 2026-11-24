using System.Numerics;
using PenguLOD.Textures;

namespace PenguLOD.Plugins.Authoring;

public enum GrassTextureMode { Auto, Basic, Packed }
public enum BillboardView { Front, Side }

/// <summary>
/// The light a billboard is rendered under: colour x (<see cref="Ambient"/> + <see cref="Direct"/> x how squarely the surface faces
/// the light). Trees and trunks are lit the way TexGen lights them, from straight above and on the lit side only: the tops of crowns
/// and branches are bright, sides and undersides get the ambient part alone. TexGen's values are 60% + 135% for trees and 150% + 50%
/// for the trunk renders of hybrid trees, whose bark faces sideways and would otherwise come out far darker than the full model.
/// Grass is <see cref="Frontal"/>: a soft light from the front and above that reaches both sides of a blade. Measured against TexGen
/// output, grass under the top light came out at half TexGen's brightness, and under this one close to it.
/// </summary>
public sealed record BillboardLight(float Ambient, float Direct, bool Frontal = false)
{
    public static readonly BillboardLight Tree = new(.6f, 1.35f), Trunk = new(1.5f, .5f), Grass = new(.7f, .3f, Frontal: true);
    private static readonly Vector3 Front = Vector3.Normalize(new Vector3(0.3f, -1f, 0.8f));

    public void Validate()
    {
        if (!float.IsFinite(Ambient) || Ambient is < 0 or > 4 || !float.IsFinite(Direct) || Direct is < 0 or > 4 || Ambient + Direct <= 0)
            throw new ArgumentException("Billboard light must be 0 to 4 for ambient and direct, and not both 0.");
    }

    /// <summary>Brightness factor for a surface with this unit normal (Z up, the viewer at -Y).</summary>
    public float Shade(Vector3 normal) => Ambient + Direct * (Frontal ? MathF.Abs(Vector3.Dot(normal, Front)) : MathF.Max(0, normal.Z));
}

public sealed record BillboardSettings(bool HdViews = true, bool ReferenceScale = true,
    float UnplacedTreeScale = 1, GrassTextureMode GrassTextures = GrassTextureMode.Auto, float ComplexGrassThreshold = .1f,
    BillboardLight? TreeLight = null, BillboardLight? GrassLight = null, BillboardLight? TrunkLight = null)
{
    public BillboardLight Trees => TreeLight ?? BillboardLight.Tree;
    public BillboardLight Grass => GrassLight ?? BillboardLight.Grass;
    public BillboardLight Trunks => TrunkLight ?? BillboardLight.Trunk;

    public void Validate()
    {
        Trees.Validate(); Grass.Validate(); Trunks.Validate();
        if (!float.IsFinite(UnplacedTreeScale) || UnplacedTreeScale is <= 0 or > 100
            || !Enum.IsDefined(GrassTextures) || !float.IsFinite(ComplexGrassThreshold) || ComplexGrassThreshold is <= 0 or > .5f)
            throw new ArgumentException("Invalid billboard scale or complex-grass detection settings.");
    }
}

/// <summary>Complex grass stores albedo/opacity above tangent normals/specular in one DDS.
/// Split before filtering so wrapping and mip reduction cannot mix the two halves.</summary>
internal static class GrassTexture
{
    internal static bool IsPacked((byte[] Rgba, int Width, int Height) image, float threshold)
    {
        if (image.Height < 2 || (image.Height & 1) != 0 || image.Width < 1) return false;
        int halfH = image.Height / 2;
        int samplesX = Math.Min(8, image.Width), samplesY = Math.Min(8, halfH);
        int tested = 0, unitNormals = 0;
        for (int sy = 0; sy < samplesY; sy++)
        {
            int py = halfH + (int)((sy + 0.5f) * halfH / samplesY);
            py = Math.Clamp(py, halfH, image.Height - 1);
            for (int sx = 0; sx < samplesX; sx++)
            {
                int px = Math.Clamp((int)((sx + 0.5f) * image.Width / samplesX), 0, image.Width - 1);
                int i = (py * image.Width + px) * 4;
                byte r = image.Rgba[i], g = image.Rgba[i + 1], b = image.Rgba[i + 2];
                if (r == 0 && g == 0 && b == 0) continue; // ignore unpopulated black padding
                tested++;
                var normal = new Vector3(r, g, b) / 127.5f - Vector3.One;
                if (normal.Z > -0.1f && MathF.Abs(normal.Length() - 1f) < threshold) unitNormals++;
            }
        }
        if (tested == 0)
        {
            int i = (image.Height - 1) * image.Width * 4;
            var normal = new Vector3(image.Rgba[i], image.Rgba[i + 1], image.Rgba[i + 2]) / 127.5f - Vector3.One;
            return MathF.Abs(normal.Length() - 1f) < threshold;
        }
        return unitNormals * 4 >= tested * 3;
    }

    internal static ((byte[] Rgba, int Width, int Height) Diffuse, (byte[] Rgba, int Width, int Height)? Normal) Decode(
        (byte[] Rgba, int Width, int Height) image, GrassTextureMode mode, float threshold)
    {
        bool packed = mode == GrassTextureMode.Packed || mode == GrassTextureMode.Auto && IsPacked(image, threshold);
        if (!packed) return (Reduce(image, normal: false), null);
        if (image.Height < 2 || (image.Height & 1) != 0)
            throw new InvalidDataException("Packed grass textures need two equal-height image halves.");
        int size = checked(image.Width * (image.Height / 2) * 4);
        var diffuse = image.Rgba.AsSpan(0, size).ToArray();
        var normal = image.Rgba.AsSpan(size, size).ToArray();
        return (Reduce((diffuse, image.Width, image.Height / 2), false), Reduce((normal, image.Width, image.Height / 2), true));
    }

    private static (byte[] Rgba, int Width, int Height) Reduce((byte[] Rgba, int Width, int Height) image, bool normal)
    {
        while (image.Width > 1024 || image.Height > 1024)
            image = TreeAtlasBuilder.Downsample(image.Rgba, image.Width, image.Height, alphaWeighted: !normal);
        if (normal) for (int i = 0; i < image.Rgba.Length; i += 4) BillboardRenderer.Renormalize(image.Rgba, i);
        return image;
    }
}
