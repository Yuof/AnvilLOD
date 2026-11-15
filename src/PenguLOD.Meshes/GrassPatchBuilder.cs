using System.Numerics;
using PenguLOD.Core.Pipeline;
using PenguLOD.Core.World;

namespace PenguLOD.Meshes;

/// <summary>
/// A grass billboard: textures plus the size and base offset from its TexGen <c>.txt</c>. With <paramref name="Rect"/> the
/// textures are the grass atlas and the quad's UVs are mapped into that rectangle (so every grass type shares one material).
/// </summary>
public sealed record GrassBillboard(string Diffuse, string? Normal, float Width, float Height, float ShiftZ, AtlasRect? Rect = null, GrassBillboard? Side = null);

/// <summary>
/// Turns the grass of one cell (from the grass cache) into LOD geometry: per kept instance two crossed, upright,
/// double-sided, alpha-tested quads (like DynDOLOD's flat 2x2 grass billboards, so they never vanish edge-on).
/// Only a sample of the instances is kept: the ground is cut into bins and each occupied bin keeps ONE tuft, as wide as the
/// grass in that bin covers (capped at the bin), so coverage is even and follows the real grass; random sampling left holes
/// and clumps that read as big blocky patches. All grass types of the cell share the bins, and the tuft is one of the bin's
/// own instances, with its type and place: binning each type on its own and putting the tuft at the middle of the bin's
/// grass piled every type's tuft onto the same grid points, which showed from afar as rows of dots on bare ground.
/// Vertex colours darken the bottom to fake the shadow inside the grass.
/// Each grass type becomes one part (one material), so a block gets one draw call per grass type.
/// Coordinates are relative to <paramref name="origin"/> (the cell's south-west corner).
/// </summary>
public static class GrassPatchBuilder
{
    public const ushort AlphaTestFlags = 0x12EC; // alpha test on, blending off
    public const byte AlphaThreshold = 128;

    /// <summary>A tuft may be this much wider than its bin (slight overlap hides the seams between bins).</summary>
    private const float BinOverlap = 1.15f;
    private const float MaxTaller = 1.6f;
    /// <summary>
    /// Default vertex-colour brightness (share of white): darker at the roots. LOD grass has none of the shading real grass gets from
    /// its neighbours, so it must be tinted well below white: at 0.85 and 0.5 a field of it was four times as bright as the real grass
    /// beside it and as bright as bare ground. DynDOLOD's defaults for ordinary grass are 0.40-0.45 and 0.20-0.225.
    /// </summary>
    public const float DefaultTop = 0.3f, DefaultBottom = 0.18f;

    /// <summary>Opaque grey vertex colour (RGBA little-endian) for a brightness between 0 and about 1.5 (values above 1 clamp to white).</summary>
    internal static uint Grey(float brightness)
    {
        uint v = (uint)Math.Clamp((int)MathF.Round(brightness * 255f), 0, 255);
        return 0xFF000000u | v << 16 | v << 8 | v;
    }

    /// <summary>Candidate bin sizes in game units; the smallest that keeps no more tufts than the density asks for is used.</summary>
    private static readonly float[] Spacings = [12, 16, 20, 24, 32, 40, 48, 64, 80, 96, 128, 160, 192, 256, 320, 384, 512, 768, 1024];

    /// <summary>One kept tuft: where, how big (scale), which way, and how many cached instances it stands for.</summary>
    internal readonly record struct Tuft(Vector3 Position, float Scale, float Yaw, int Count, float Spacing);

    /// <param name="tiers">Split the tufts into this many density tiers (see <see cref="SplitTiers"/>), each its own part, so the game can show a first n of them.</param>
    public static LodMesh Build(string path, Vector3 origin, IEnumerable<(GrassBillboard Billboard, IReadOnlyList<GrassInstance> Instances)> types,
        float density, float sizeScale, float top = DefaultTop, float bottom = DefaultBottom, int tiers = 1)
    {
        uint bottomColor = Grey(bottom), topColor = Grey(top);

        // Types that share textures (the atlas: all of them) go into the same parts, so a block needs few draw calls.
        var groups = new Dictionary<string, (LodMaterial Material, List<(GrassBillboard Bb, Tuft T)> Items)>(StringComparer.Ordinal);
        var all = types.ToList();
        foreach (var byType in StratifyTypes(all.Select(t => t.Instances).ToList(), origin, density).GroupBy(k => k.Type).OrderBy(g => g.Key))
        {
            var bb = all[byType.Key].Billboard;
            var kept = byType.Select(k => k.Tuft).ToList();
            var key = bb.Diffuse + "|" + bb.Normal;
            if (!groups.TryGetValue(key, out var group))
            {
                var material = new LodMaterial(
                    [bb.Diffuse, bb.Normal ?? "", "", "", "", "", "", "", ""],
                    0x8000_0300u,
                    0x0000_0005u | 0x10u, // double-sided
                    3,
                    true, AlphaTestFlags, AlphaThreshold,
                    Vector3.Zero, 1f);
                groups[key] = group = (material, []);
            }
            foreach (var t in kept) group.Items.Add((bb, t));
        }

        var parts = new List<LodMeshPart>();
        foreach (var (material, grouped) in groups.Values)
        foreach (var (tier, items) in SplitTiers(grouped, origin, tiers))
        {
            // Split so no part exceeds 65,535 vertices (8 per tuft: two crossed quads).
            const int MaxQuads = 8_000;
            for (int start = 0; start < items.Count; start += MaxQuads)
            {
                int n = Math.Min(MaxQuads, items.Count - start);
                var pos = new Vector3[n * 8];
                var uv = new Vector2[n * 8];
                var nrm = new Vector3[n * 8];
                var tan = new Vector3[n * 8];
                var bit = new Vector3[n * 8];
                var col = new uint[n * 8];
                var tris = new ushort[n * 12];
                for (int q = 0; q < n; q++)
                {
                    var (bb, g) = items[start + q];
                    for (int c = 0; c < 2; c++)
                    {
                        var view = c == 1 && bb.Side is { } sideView && sideView.Diffuse == bb.Diffuse && sideView.Normal == bb.Normal ? sideView : bb;
                        float s = g.Scale * sizeScale;
                        float w0 = view.Width * s;
                        // As wide as the grass in the bin would cover, but never wider than the bin itself.
                        float w = w0 <= 0 ? 0 : Math.Clamp(w0 * MathF.Sqrt(g.Count), w0, MathF.Max(w0, g.Spacing * BinOverlap));
                        float taller = w0 <= 0 ? 1f : MathF.Min(w / w0, MaxTaller);
                        float hw = w * 0.5f, h = view.Height * s * taller;
                        var basePos = g.Position - origin + new Vector3(0, 0, view.ShiftZ * g.Scale);
                        var (u0, v0, u1, v1) = view.Rect is { } r ? (r.U0, r.V0, r.U1, r.V1) : (0f, 0f, 1f, 1f);
                        float yaw = g.Yaw + c * MathF.PI * 0.5f;
                        var dir = new Vector3(MathF.Cos(yaw), MathF.Sin(yaw), 0);
                        int v = q * 8 + c * 4;
                        pos[v] = basePos - dir * hw;
                        pos[v + 1] = basePos + dir * hw;
                        pos[v + 2] = basePos + dir * hw + new Vector3(0, 0, h);
                        pos[v + 3] = basePos - dir * hw + new Vector3(0, 0, h);
                        uv[v] = new Vector2(u0, v1); uv[v + 1] = new Vector2(u1, v1); uv[v + 2] = new Vector2(u1, v0); uv[v + 3] = new Vector2(u0, v0);
                        // "Sphere" normals (DynDOLOD's grass templates do the same): mostly up, leaning out to the sides,
                        // so the tuft is lit like a soft clump instead of a flat card.
                        var side = Vector3.Cross(Vector3.UnitZ, dir);
                        nrm[v] = Vector3.Normalize(Vector3.UnitZ - dir * 0.5f);
                        nrm[v + 1] = Vector3.Normalize(Vector3.UnitZ + dir * 0.5f);
                        nrm[v + 2] = Vector3.Normalize(Vector3.UnitZ + dir * 0.35f);
                        nrm[v + 3] = Vector3.Normalize(Vector3.UnitZ - dir * 0.35f);
                        for (int k = 0; k < 4; k++) { tan[v + k] = dir; bit[v + k] = side; }
                        col[v] = col[v + 1] = bottomColor;
                        col[v + 2] = col[v + 3] = topColor;
                        int t = q * 12 + c * 6;
                        tris[t] = (ushort)v; tris[t + 1] = (ushort)(v + 1); tris[t + 2] = (ushort)(v + 2);
                        tris[t + 3] = (ushort)v; tris[t + 4] = (ushort)(v + 2); tris[t + 5] = (ushort)(v + 3);
                    }
                }
                parts.Add(new LodMeshPart
                {
                    Material = material, Positions = pos, UVs = uv, Normals = nrm, Tangents = tan, Bitangents = bit, Colors = col, Triangles = tris, Tier = tier,
                });
            }
        }
        return new LodMesh { Path = path, Parts = parts };
    }

    /// <summary>
    /// Deals the tufts into <paramref name="tiers"/> tiers: sorted along a Morton (Z-order) curve over the ground, every n-th tuft goes to the
    /// same tier. Every tier is then an even spread over the whole patch, so showing only the first few thins the grass evenly instead of
    /// leaving bald squares. One tier (or none to split) keeps the tufts in their order, as before.
    /// </summary>
    internal static IEnumerable<(int Tier, List<(GrassBillboard Bb, Tuft T)> Items)> SplitTiers(List<(GrassBillboard Bb, Tuft T)> items, Vector3 origin, int tiers)
    {
        if (tiers <= 1 || items.Count == 0)
        {
            yield return (0, items);
            yield break;
        }
        var sorted = items.OrderBy(i => Morton(i.T.Position, origin)).ToList();
        var split = new List<(GrassBillboard, Tuft)>[tiers];
        for (int t = 0; t < tiers; t++) split[t] = [];
        for (int i = 0; i < sorted.Count; i++) split[i % tiers].Add(sorted[i]);
        for (int t = 0; t < tiers; t++)
            if (split[t].Count > 0) yield return (t, split[t]);
    }

    /// <summary>Z-order code of a position relative to the patch origin, one unit per step (a patch is at most 16 cells wide).</summary>
    internal static uint Morton(Vector3 p, Vector3 origin)
    {
        static uint Spread(uint v)
        {
            v &= 0xFFFF;
            v = (v | v << 8) & 0x00FF00FF;
            v = (v | v << 4) & 0x0F0F0F0F;
            v = (v | v << 2) & 0x33333333;
            v = (v | v << 1) & 0x55555555;
            return v;
        }
        uint x = (uint)Math.Clamp((int)(p.X - origin.X), 0, 65535), y = (uint)Math.Clamp((int)(p.Y - origin.Y), 0, 65535);
        return Spread(x) | Spread(y) << 1;
    }

    /// <summary>The tufts for one grass type on its own (see <see cref="StratifyTypes"/>).</summary>
    internal static List<Tuft> Stratify(IReadOnlyList<GrassInstance> instances, Vector3 origin, float density)
        => StratifyTypes([instances], origin, density).Select(k => k.Tuft).ToList();

    /// <summary>
    /// Picks the tufts for the grass of one cell: bins of the smallest candidate size that yields no more tufts than
    /// <c>density</c> x instances (all types together), and from each occupied bin one of its instances, with its type, place,
    /// size and direction: the one nearest a point chosen for the bin within <see cref="Jitter"/> of its middle. The point comes
    /// from a hash of the bin, so the tufts do not line up in rows, stay about a bin apart, a type is kept about as often as it
    /// occurs there, and re-runs keep the same tufts.
    /// </summary>
    internal static List<(int Type, Tuft Tuft)> StratifyTypes(IReadOnlyList<IReadOnlyList<GrassInstance>> types, Vector3 origin, float density)
    {
        var result = new List<(int, Tuft)>();
        int n = types.Sum(t => t.Count);
        if (n == 0) return result;
        if (density >= 1f)
        {
            for (int t = 0; t < types.Count; t++)
                foreach (var g in types[t]) result.Add((t, new Tuft(g.Position, g.Scale, g.Yaw, 1, 0f)));
            return result;
        }

        int target = Math.Max(1, (int)MathF.Round(n * density));
        // Occupied-bin count only falls as the bins grow, so binary-search the candidates.
        int lo = 0, hi = Spacings.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (OccupiedBins(types, origin, Spacings[mid]) <= target) hi = mid; else lo = mid + 1;
        }
        float spacing = Spacings[lo];

        var bins = new Dictionary<long, Bin>();
        for (int t = 0; t < types.Count; t++)
            foreach (var g in types[t])
            {
                long key = Key(g.Position, origin, spacing);
                ref var b = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(bins, key, out bool known);
                if (!known) b.Aim = Aim(key, origin, spacing);
                float distance = Vector2.DistanceSquared(new Vector2(g.Position.X, g.Position.Y), b.Aim);
                if (b.Count == 0 || distance < b.Distance) { b.Distance = distance; b.Type = t; b.Pick = g; }
                b.Count++;
            }
        foreach (var (_, b) in bins.OrderBy(kv => kv.Key))
            result.Add((b.Type, new Tuft(b.Pick.Position, b.Pick.Scale, b.Pick.Yaw, b.Count, spacing)));
        return result;
    }

    /// <summary>How far from the middle of its bin a tuft may be aimed, as a share of the bin's size, each way.</summary>
    private const float Jitter = 0.3f;

    /// <summary>The point a bin's tuft is aimed at: its middle, moved by up to <see cref="Jitter"/> of its size each way (SplitMix64 of the bin).</summary>
    private static Vector2 Aim(long key, Vector3 origin, float spacing)
    {
        ulong x = (ulong)key + 0x9E3779B97F4A7C15UL;
        x = (x ^ x >> 30) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ x >> 27) * 0x94D049BB133111EBUL;
        x ^= x >> 31;
        float jx = ((x & 0xFFFF) / 65535f * 2 - 1) * Jitter, jy = ((x >> 16 & 0xFFFF) / 65535f * 2 - 1) * Jitter;
        long ix = (key >> 32) - 4096, iy = (long)(uint)key - 4096;
        return new Vector2(origin.X + (ix + 0.5f + jx) * spacing, origin.Y + (iy + 0.5f + jy) * spacing);
    }

    private struct Bin { public Vector2 Aim; public float Distance; public int Type; public GrassInstance Pick; public int Count; }

    private static long Key(Vector3 p, Vector3 origin, float spacing)
        => ((long)MathF.Floor((p.X - origin.X) / spacing) + 4096) << 32 | (uint)((long)MathF.Floor((p.Y - origin.Y) / spacing) + 4096);

    private static int OccupiedBins(IReadOnlyList<IReadOnlyList<GrassInstance>> types, Vector3 origin, float spacing)
    {
        var seen = new HashSet<long>();
        foreach (var instances in types)
            foreach (var g in instances) seen.Add(Key(g.Position, origin, spacing));
        return seen.Count;
    }
}
