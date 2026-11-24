using System.Numerics;
using System.Text.RegularExpressions;

namespace PenguLOD.Meshes;

/// <summary>
/// Fits a mountain LOD mesh to its full model. Mountain mods ship LOD meshes that are decimated copies of the full mesh, and many of their vertices
/// lie outside the real surface by far more than any single inset can cover (Enhanced Rocks and Mountains: at LOD4 about 16% of the vertices of
/// MountainCliff04 are over 16 units outside, at LOD8 about a third). Where the engine draws the LOD and the full model together, those
/// stretches z-fight. Each vertex that lies outside is moved onto the nearest point of the full model's surface; vertices already inside stay.
/// The inset (<see cref="LodInset"/>) then pulls the whole mesh a little inside, evenly.
/// </summary>
public static class MountainFit
{
    /// <summary>A vertex further outside than this is not moved: the LOD is a different shape there, not a coarser copy.</summary>
    public const float MaxMove = 80f;

    private static readonly Regex LodName = new(@"^(?<dir>(?:.*\\)?)lod\\mountains\\(?<name>[^\\]+?)_lod_(?:0[hl]?|1)\.nif$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The full model a mountain LOD mesh (LOD4 or LOD8 variant) was cut from, or null when the path is not one. Paths are relative to meshes\.</summary>
    public static string? FullModelFor(string lodPath)
    {
        var m = LodName.Match(lodPath.Replace('/', '\\').TrimStart('\\'));
        return m.Success ? m.Groups["dir"].Value + "landscape\\mountains\\" + m.Groups["name"].Value + ".nif" : null;
    }

    /// <summary>The LOD mesh with every vertex outside the full model moved onto its surface. Returns <paramref name="lod"/> itself when nothing moved.</summary>
    public static LodMesh Fit(LodMesh lod, LodMesh full, out int moved)
    {
        moved = 0;
        var tris = new List<(Vector3 A, Vector3 B, Vector3 C, Vector3 N)>();
        foreach (var p in full.Parts)
            for (int t = 0; t + 2 < p.Triangles.Length; t += 3)
            {
                int i0 = p.Triangles[t], i1 = p.Triangles[t + 1], i2 = p.Triangles[t + 2];
                var a = p.Positions[i0]; var b = p.Positions[i1]; var c = p.Positions[i2];
                var n = Vector3.Cross(b - a, c - a);
                if (n.LengthSquared() < 1e-9f) continue;
                n = Vector3.Normalize(n);
                if (Vector3.Dot(n, p.Normals[i0] + p.Normals[i1] + p.Normals[i2]) < 0) n = -n; // outward, by the stored vertex normals
                tris.Add((a, b, c, n));
            }
        if (tris.Count == 0) return lod;

        var grid = new TriangleGrid(tris);
        var seenStamp = new int[tris.Count];
        int queryId = 0;

        var parts = new List<LodMeshPart>(lod.Parts.Count);
        var cache = new Dictionary<Vector3, Vector3>();
        foreach (var part in lod.Parts)
        {
            var pos = (Vector3[])part.Positions.Clone();
            bool changed = false;
            for (int i = 0; i < pos.Length; i++)
            {
                if (!cache.TryGetValue(pos[i], out var target))
                {
                    target = grid.Nearest(pos[i], seenStamp, ref queryId);
                    cache[pos[i]] = target;
                }
                if (target != pos[i]) { pos[i] = target; changed = true; moved++; }
            }
            parts.Add(changed ? CopyWith(part, pos) : part);
        }
        return moved == 0 ? lod : new LodMesh { Path = lod.Path, Parts = parts, Warnings = lod.Warnings, NoShapesInFile = lod.NoShapesInFile };
    }

    /// <summary>
    /// 3D spatial grid over full-model triangles with <c>CellSize = 96 &gt; MaxMove (80)</c>, guaranteeing that any surface point
    /// within <see cref="MaxMove"/> of query vertex <c>v</c> lies inside the 3x3x3 cell neighbourhood around <c>v</c>.
    /// </summary>
    private sealed class TriangleGrid
    {
        private const float CellSize = 96f;
        private const float InvCell = 1f / CellSize;
        private readonly List<(Vector3 A, Vector3 B, Vector3 C, Vector3 N)> _tris;
        private readonly Dictionary<(int X, int Y, int Z), List<int>> _cells = [];
        private readonly List<int> _oversized = [];

        public TriangleGrid(List<(Vector3 A, Vector3 B, Vector3 C, Vector3 N)> tris)
        {
            _tris = tris;
            for (int i = 0; i < tris.Count; i++)
            {
                var (a, b, c, _) = tris[i];
                int x0 = (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)) * InvCell);
                int x1 = (int)MathF.Floor(MathF.Max(a.X, MathF.Max(b.X, c.X)) * InvCell);
                int y0 = (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) * InvCell);
                int y1 = (int)MathF.Floor(MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) * InvCell);
                int z0 = (int)MathF.Floor(MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) * InvCell);
                int z1 = (int)MathF.Floor(MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) * InvCell);
                long span = (long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1);
                if (span > 512) { _oversized.Add(i); continue; }
                for (int gz = z0; gz <= z1; gz++)
                    for (int gy = y0; gy <= y1; gy++)
                        for (int gx = x0; gx <= x1; gx++)
                        {
                            if (!_cells.TryGetValue((gx, gy, gz), out var list)) _cells[(gx, gy, gz)] = list = [];
                            list.Add(i);
                        }
            }
        }

        public Vector3 Nearest(Vector3 v, int[] seenStamp, ref int queryId)
        {
            int qid = ++queryId;
            if (qid == int.MaxValue) { Array.Clear(seenStamp); qid = queryId = 1; }
            float best = (MaxMove + 1f) * (MaxMove + 1f);
            Vector3 point = v;
            float side = 0;

            void Test(int idx)
            {
                if (seenStamp[idx] == qid) return;
                seenStamp[idx] = qid;
                var t = _tris[idx];
                var q = ClosestOnTriangle(v, t.A, t.B, t.C);
                float d = Vector3.DistanceSquared(v, q);
                if (d < best) { best = d; point = q; side = Vector3.Dot(v - q, t.N); }
            }

            foreach (int idx in _oversized) Test(idx);
            int cx = (int)MathF.Floor(v.X * InvCell), cy = (int)MathF.Floor(v.Y * InvCell), cz = (int)MathF.Floor(v.Z * InvCell);
            for (int dz = -1; dz <= 1; dz++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        if (_cells.TryGetValue((cx + dx, cy + dy, cz + dz), out var bucket))
                            foreach (int idx in bucket) Test(idx);

            float dist = MathF.Sqrt(best);
            return side > 1e-3f && dist <= MaxMove ? point : v;
        }
    }

    private static LodMeshPart CopyWith(LodMeshPart p, Vector3[] positions) => p with { Positions = positions };

    /// <summary>Closest point of triangle ABC to P (Ericson, Real-Time Collision Detection).</summary>
    internal static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a; var ac = c - a; var ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        var bp = p - b; float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + d1 / (d1 - d3) * ab;
        var cp = p - c; float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + d2 / (d2 - d6) * ac;
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (d4 - d3) / (d4 - d3 + (d5 - d6)) * (c - b);
        float denom = 1f / (va + vb + vc);
        return a + ab * (vb * denom) + ac * (vc * denom);
    }
}
