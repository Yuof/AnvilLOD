using System.Numerics;
using PenguLOD.Core.Pipeline;
using PenguLOD.Core.World;
using PenguLOD.Meshes.Authoring;

namespace PenguLOD.Meshes;

/// <summary>
/// Two-stage adaptive terrain mesh simplifier:
/// 1) Full-resolution LAND vertex sampling (<c>(32 * level + 1)^2</c>, an exact <c>(2^k + 1)^2</c> grid at LOD4/8/16/32)
///    reduced via a crack-free Martini Right-Triangulated Irregular Network (RTIN) pre-pass.
/// 2) Garland-Heckbert half-edge QEM refinement (<see cref="MeshSimplifier"/>) with optional pinned cell borders and
///    submerged-seafloor error relaxation (<c>OptimizeUnseen</c>).
/// </summary>
public static class TerrainMeshSimplifier
{
    private const int MaxNifCount = 62000;
    private const float UnderwaterClearance = 512f;
    private const float UnderwaterRelaxFactor = 16f;

    private static readonly LodMaterial DummyMaterial = new(
        ["", "", "", "", "", "", "", "", ""], 0, 0, 3, false, 0, 0, Vector3.Zero, 1f);

    public static (Vector3[] Positions, ushort[] Indices) BuildAdaptive(
        QuadKey tile,
        Func<float, float, float?> sample,
        float worldMaxError,
        bool protectBorders = true,
        bool optimizeUnseen = true,
        Func<int, int, float?>? water = null,
        CancellationToken ct = default)
    {
        int level = (int)tile.Level;
        int gridSize = level * 32; // 128 (2^7), 256 (2^8), 512 (2^9), 1024 (2^10)
        int k = BitOperations.Log2((uint)gridSize);
        int side = gridSize + 1;
        float span = level * CellCoord.CellSize;
        float baseX = tile.X * CellCoord.CellSize, baseY = tile.Y * CellCoord.CellSize;

        var heights = new float[side * side];
        for (int y = 0; y < side; y++)
        {
            ct.ThrowIfCancellationRequested();
            float wy = baseY + span * y / gridSize;
            for (int x = 0; x < side; x++)
            {
                float wx = baseX + span * x / gridSize;
                float h = sample(wx, wy) ?? throw new InvalidDataException($"Missing terrain at {tile.Worldspace} ({wx}, {wy}); supply landscape records or existing terrain LOD.");
                if (!float.IsFinite(h)) throw new InvalidDataException("Terrain contains a non-finite height.");
                heights[y * side + x] = h;
            }
        }

        // Optional underwater mask: relax error tolerance on deeply submerged seafloors covered by opaque LOD water.
        bool[]? submerged = null;
        if (optimizeUnseen && water is not null)
        {
            var cellWater = new float?[level, level];
            bool anyWater = false;
            for (int cy = 0; cy < level; cy++)
                for (int cx = 0; cx < level; cx++)
                    if (water(tile.X + cx, tile.Y + cy) is { } wh && float.IsFinite(wh))
                    {
                        cellWater[cx, cy] = wh;
                        anyWater = true;
                    }
            if (anyWater)
            {
                submerged = new bool[side * side];
                for (int y = 1; y < gridSize; y++)
                {
                    int cy = Math.Min(y / 32, level - 1);
                    for (int x = 1; x < gridSize; x++)
                    {
                        int cx = Math.Min(x / 32, level - 1);
                        if (cellWater[cx, cy] is not { } wh) continue;
                        float limit = wh - UnderwaterClearance;
                        if (heights[y * side + x] < limit
                            && heights[(y - 1) * side + x] < limit && heights[(y + 1) * side + x] < limit
                            && heights[y * side + x - 1] < limit && heights[y * side + x + 1] < limit)
                            submerged[y * side + x] = true;
                    }
                }
            }
        }

        // Stage A: Bottom-up Martini RTIN error propagation in strict reverse level-order (leaves -> roots).
        var errors = new float[side * side];
        int numNonLeaf = 2 * gridSize * gridSize - 2; // ids 2 .. 2*G*G - 1
        int smallestInternalMsb = 2 * k;
        float pinSentinel = MathF.Max(worldMaxError * 64f, 1_000_000f);

        for (int id = numNonLeaf + 1; id >= 2; id--)
        {
            if ((id & 16383) == 0) ct.ThrowIfCancellationRequested();
            int msb = BitOperations.Log2((uint)id);
            int ax, ay, bx, by, cx, cy;
            if (((id >> (msb - 1)) & 1) == 0)
            {
                ax = gridSize; ay = 0; bx = 0; by = gridSize; cx = 0; cy = 0;
            }
            else
            {
                ax = 0; ay = gridSize; bx = gridSize; by = 0; cx = gridSize; cy = gridSize;
            }
            for (int bit = msb - 2; bit >= 0; bit--)
            {
                int smx = (ax + bx) >> 1, smy = (ay + by) >> 1;
                if (((id >> bit) & 1) == 0)
                {
                    bx = ax; by = ay; ax = cx; ay = cy; cx = smx; cy = smy;
                }
                else
                {
                    ax = bx; ay = by; bx = cx; by = cy; cx = smx; cy = smy;
                }
            }

            int mx = (ax + bx) >> 1, my = (ay + by) >> 1;
            int midIdx = my * side + mx;
            float interpolated = 0.5f * (heights[ay * side + ax] + heights[by * side + bx]);
            float err = MathF.Abs(heights[midIdx] - interpolated);
            if (submerged is not null && submerged[midIdx])
                err /= UnderwaterRelaxFactor;
            if (protectBorders && level == 4 && (mx == 0 || mx == gridSize || my == 0 || my == gridSize))
                err = pinSentinel;

            if (msb < smallestInternalMsb)
            {
                int lcx = (ax + cx) >> 1, lcy = (ay + cy) >> 1;
                int rcx = (bx + cx) >> 1, rcy = (by + cy) >> 1;
                err = MathF.Max(err, MathF.Max(errors[lcy * side + lcx], errors[rcy * side + rcx]));
            }
            if (err > errors[midIdx]) errors[midIdx] = err;
        }

        // Extract adaptive RTIN triangulation within 0.5 * worldMaxError, raising threshold if needed to fit 16-bit NIF counts.
        float rtinThreshold = MathF.Max(0.25f, worldMaxError * 0.5f);
        var vertexRemap = new int[side * side];
        var positions = new List<Vector3>(4096);
        var tris = new List<int>(8192);

        for (int attempt = 0; attempt < 16; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            Array.Fill(vertexRemap, -1);
            positions.Clear();
            tris.Clear();
            ExtractTriangle(gridSize, 0, 0, gridSize, 0, 0);
            ExtractTriangle(0, gridSize, gridSize, 0, gridSize, gridSize);
            if (positions.Count <= MaxNifCount && tris.Count / 3 <= MaxNifCount) break;
            rtinThreshold = MathF.Max(rtinThreshold * 1.5f, 1f);
        }

        void ExtractTriangle(int ax, int ay, int bx, int by, int cx, int cy)
        {
            int mx = (ax + bx) >> 1, my = (ay + by) >> 1;
            if (Math.Abs(ax - cx) + Math.Abs(ay - cy) > 1 && errors[my * side + mx] > rtinThreshold)
            {
                ExtractTriangle(cx, cy, ax, ay, mx, my);
                ExtractTriangle(bx, by, cx, cy, mx, my);
                return;
            }
            // CCW (+Z) winding: (c, a, b)
            tris.Add(UseVertex(cx, cy));
            tris.Add(UseVertex(ax, ay));
            tris.Add(UseVertex(bx, by));
        }

        int UseVertex(int gx, int gy)
        {
            int idx = gy * side + gx;
            int v = vertexRemap[idx];
            if (v < 0)
            {
                v = positions.Count;
                vertexRemap[idx] = v;
                // Stored in tile-divided coordinates (multiplied by shape scale = level in engine).
                positions.Add(new Vector3(
                    span * gx / gridSize / level,
                    span * gy / gridSize / level,
                    heights[idx] / level));
            }
            return v;
        }

        var ushortTris = new ushort[tris.Count];
        for (int i = 0; i < tris.Count; i++) ushortTris[i] = checked((ushort)tris[i]);

        // Stage B: Heightfield QEM refinement in tile-divided space (worldMaxError / level).
        float qemMaxError = worldMaxError / level;
        if (qemMaxError > 0f && ushortTris.Length / 3 > 2)
        {
            int nv = positions.Count;
            var posArr = positions.ToArray();
            var uvs = new Vector2[nv];
            var normals = new Vector3[nv];
            for (int i = 0; i < nv; i++)
            {
                uvs[i] = new Vector2(posArr[i].X / CellCoord.CellSize, 1f - posArr[i].Y / CellCoord.CellSize);
                normals[i] = Vector3.UnitZ;
            }
            var part = new LodMeshPart
            {
                Material = DummyMaterial,
                Positions = posArr,
                UVs = uvs,
                Normals = normals,
                Tangents = normals,
                Bitangents = normals,
                Triangles = ushortTris,
            };
            bool pinAllBorders = protectBorders && level == 4;
            var simplified = MeshSimplifier.Simplify(part, qemMaxError, minTriangles: 2, pinBorders: pinAllBorders);
            return (simplified.Positions, simplified.Triangles);
        }

        return (positions.ToArray(), ushortTris);
    }
}
