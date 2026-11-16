using System.Collections.Concurrent;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PenguLOD.Core.Pipeline;
using PenguLOD.Core.World;
using PenguLOD.Meshes;
using PenguLOD.Textures;
using PenguLOD.Textures.Dds;

namespace PenguLOD.Plugins;

/// <summary>Packs shader-compatible object surfaces; unsupported materials retain their original sampling.</summary>
public static class ObjectAtlasStage
{
    public const int Version = 4; // surfaces are folded into one tile triangle by triangle; pages up to 4096 px
    /// <summary>Largest page side in pixels, the largest texture the texture code makes. At the default cell size a page holds up to 256 materials.</summary>
    public const int PageSize = 4096;
    public sealed record Report(int PackedMaterials, int Pages, int EligibleParts, int SkippedParts,
        IReadOnlyDictionary<string, int> Reasons, int SourceMaterials = 0, int AtlasMaterials = 0, long TextureBytes = 0,
        IReadOnlyList<string>? Warnings = null);
    public sealed record Mapping(string Diffuse, string Normal, AtlasRect Rect, IReadOnlyDictionary<int, string>? Extra = null);
    public sealed record Result(Report Report, Func<LodMesh, LodMesh> Transform);
    private sealed record Candidate(LodMaterial Material, string Group);
    private sealed record SourceInfo(DdsFile.Info Info, string Hash);
    private static readonly int[] ExtraSlots = [2, 3, 5, 7, 8];

    public static string? Ineligible(LodMeshPart part)
    {
        var m = part.Material;
        if (m.Passthru is not null || m.RenderEffect is not null) return "passthrough shader";
        if (m.Textures.Count == 0 || string.IsNullOrWhiteSpace(m.Textures[0])) return "no diffuse texture";
        if (m.Textures.Count > 9 || !string.IsNullOrWhiteSpace(m.Textures.ElementAtOrDefault(6))) return "unsupported shader channels";
        // Projected UVs, parallax displacement, refraction and multilayer sampling are not affine UV lookups.
        if ((m.ShaderFlags1 & ((1u << 11) | (1u << 15) | (1u << 16) | (1u << 23) | (1u << 28))) != 0
            || (m.ShaderFlags2 & (1u << 24)) != 0) return "non-affine shader sampling";
        if (m.HasAlpha && ObjectTextureAssets.AlphaCutoff(m) is null) return "unsupported alpha blend/test";
        if (TriangleShifts(part) is not { } shifts || FoldedVertices(part, shifts) > ushort.MaxValue) return "tiling or invalid UVs";
        return null;
    }

    /// <summary>How far a triangle may reach past its tile, in tiles: the rounding of half-precision texture coordinates near a tile edge.</summary>
    private const float FoldTolerance = 0.002f;

    /// <summary>
    /// The whole-tile shift that brings each triangle's texture coordinates into the unit square, or null when some triangle does
    /// not fit one tile (it crosses a tile edge or repeats the texture) or the coordinates are unusable. Under wrapping, a triangle
    /// in tile (2, -1) samples exactly what it would in tile (0, 0), so each triangle can be moved on its own; a surface mapped
    /// across several tiles in separate pieces, as most LOD models of walls and rock are, fits an atlas this way. An axis that
    /// clamps is never shifted. Triangles are moved whole, never single vertices: that would stretch a triangle across a tile edge.
    /// </summary>
    private static Vector2[]? TriangleShifts(LodMeshPart part)
    {
        var uvs = part.UVs; var triangles = part.Triangles;
        if (part.Material.ClampMode > 3 || uvs.Length != part.Positions.Length || uvs.Length == 0 || triangles.Length % 3 != 0) return null;
        bool wrapU = (part.Material.ClampMode & 2) != 0, wrapV = (part.Material.ClampMode & 1) != 0;
        var shifts = new Vector2[triangles.Length / 3];
        for (int t = 0; t < shifts.Length; t++)
        {
            int a = triangles[t * 3], b = triangles[t * 3 + 1], c = triangles[t * 3 + 2];
            if (a >= uvs.Length || b >= uvs.Length || c >= uvs.Length) return null;
            var min = Vector2.Min(uvs[a], Vector2.Min(uvs[b], uvs[c])); var max = Vector2.Max(uvs[a], Vector2.Max(uvs[b], uvs[c]));
            if (!float.IsFinite(min.X) || !float.IsFinite(min.Y) || !float.IsFinite(max.X) || !float.IsFinite(max.Y)) return null;
            var shift = new Vector2(wrapU ? MathF.Floor(min.X + FoldTolerance) : 0, wrapV ? MathF.Floor(min.Y + FoldTolerance) : 0);
            if (min.X - shift.X < -FoldTolerance || min.Y - shift.Y < -FoldTolerance
                || max.X - shift.X > 1 + FoldTolerance || max.Y - shift.Y > 1 + FoldTolerance) return null;
            shifts[t] = shift;
        }
        return shifts;
    }

    /// <summary>Vertices the part has once every vertex used under two different shifts is doubled.</summary>
    private static int FoldedVertices(LodMeshPart part, Vector2[] shifts)
    {
        if (Uniform(shifts)) return part.VertexCount;
        var seen = new HashSet<(int, float, float)>();
        for (int i = 0; i < part.Triangles.Length; i++) seen.Add((part.Triangles[i], shifts[i / 3].X, shifts[i / 3].Y));
        return seen.Count;
    }

    private static bool Uniform(Vector2[] shifts)
    {
        for (int i = 1; i < shifts.Length; i++) if (shifts[i] != shifts[0]) return false;
        return true;
    }

    /// <summary>
    /// The part with its texture coordinates folded into the unit square and passed through <paramref name="place"/>. When every
    /// triangle has the same shift the vertices are kept as they are; otherwise a vertex shared by triangles of different tiles
    /// becomes one vertex per tile, with the same position, normal and colour.
    /// </summary>
    private static LodMeshPart Fold(LodMeshPart p, LodMaterial material, Vector2[] shifts, Func<Vector2, Vector2> place)
    {
        if (Uniform(shifts))
        {
            var shift = shifts.Length == 0 ? Vector2.Zero : shifts[0];
            return ObjectTextureAssets.With(p, material, p.UVs.Select(v => place(v - shift)).ToArray());
        }
        var index = new Dictionary<(int, float, float), ushort>();
        var source = new List<int>(p.VertexCount); var uvs = new List<Vector2>(p.VertexCount);
        var triangles = new ushort[p.Triangles.Length];
        for (int i = 0; i < triangles.Length; i++)
        {
            int vertex = p.Triangles[i]; var shift = shifts[i / 3];
            if (!index.TryGetValue((vertex, shift.X, shift.Y), out ushort folded))
            {
                index[(vertex, shift.X, shift.Y)] = folded = (ushort)source.Count;
                source.Add(vertex); uvs.Add(place(p.UVs[vertex] - shift));
            }
            triangles[i] = folded;
        }
        T[] Pick<T>(T[] values) => source.Select(v => values[v]).ToArray();
        return p with
        {
            Material = material, Positions = Pick(p.Positions), UVs = [.. uvs], Normals = Pick(p.Normals), Tangents = Pick(p.Tangents),
            Bitangents = Pick(p.Bitangents), Colors = p.Colors is null ? null : Pick(p.Colors), Triangles = triangles,
        };
    }

    public static Result Build(IEnumerable<LodMesh> meshes, AssetIndex assets, string output, int cellSize = 256,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (cellSize is < 64 or > 512 || (cellSize & (cellSize - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(cellSize));
        Directory.CreateDirectory(output);
        var source = new ObjectTextureAssets(assets, output);
        var materials = new SortedDictionary<string, Candidate>(StringComparer.Ordinal);
        var info = new Dictionary<string, SourceInfo>(StringComparer.OrdinalIgnoreCase);
        var failures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var reasons = new Dictionary<string, int>(); var warnings = new List<string>();
        int eligible = 0, skipped = 0;
        var modelArray = meshes.ToArray();
        foreach (var mesh in modelArray) foreach (var part in mesh.Parts)
        {
            ct.ThrowIfCancellationRequested(); RunLimits.Check();
            string? reason = Ineligible(part);
            var m = part.Material;
            if (reason is null)
            {
                try
                {
                    var slots = ShaderGuard.TextureSlots(m.Textures);
                    foreach (int slot in new[] { 0, 1 }.Concat(ExtraSlots))
                    {
                        string path = slots[slot]; if (path.Length == 0) continue;
                        if (failures.TryGetValue(path, out var failure)) throw new InvalidDataException(failure);
                        if (info.ContainsKey(path)) continue;
                        try
                        {
                            var bytes = source.Read(path); var header = LodTextureEncoding.Read2D(bytes);
                            if (header.DxgiFormat is 27 or 70 or 73 or 76 or 79 or 81 or 82 or 84 or 90 or 97
                                || (header.Format == PenguLOD.Textures.Bc.BcFormat.Bc5 && slot != 1))
                                throw new NotSupportedException("Signed, typeless or two-channel data cannot be atlased faithfully.");
                            // Decode once here to validate the full top level, without keeping all source images resident.
                            _ = LodTextureEncoding.Decode(bytes);
                            info[path] = new(header, Convert.ToHexString(SHA256.HashData(bytes)));
                        }
                        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or InvalidDataException)
                        { failures[path] = ex.Message; warnings.Add(path + ": " + ex.Message); throw; }
                    }
                    var groupSlots = slots.Select((p, i) => i == 4 ? p : i < 2 || p.Length > 0 ? $"slot{i}" : "").ToArray();
                    string group = (m with { Textures = groupSlots, ClampMode = 0 }).Key
                        + "|lod:" + ObjectTextureAssets.IsLodTexture(m)
                        + "|srgb:" + string.Join(',', slots.Select((p, i) => i != 4 && p.Length > 0 && LodTextureEncoding.IsSrgb(info[p].Info) ? i.ToString() : ""));
                    materials[m.Key] = new(m, group);
                }
                catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or InvalidDataException)
                { reason = "unavailable or unsupported texture"; }
            }
            if (reason is null) eligible++;
            else { skipped++; reasons[reason] = reasons.GetValueOrDefault(reason) + 1; }
        }
        var mapped = new Dictionary<string, Mapping>(StringComparer.Ordinal);
        int pages = 0; long textureBytes = 0;
        // One page per group of compatible materials wherever it fits: shapes merge only when they share a page, and with 2048 px
        // pages of 64 cells a block drew its atlased surfaces from a couple of dozen pages.
        int perPage = (PageSize / cellSize) * (PageSize / cellSize);
        // Shader/alpha/emittance/cubemap compatibility comes before path ordering, so each page can actually batch.
        foreach (var group in materials.GroupBy(kv => kv.Value.Group).OrderBy(g => g.Key, StringComparer.Ordinal))
        foreach (var batch in group.Chunk(perPage))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Object atlas: {mapped.Count:N0}/{materials.Count:N0} materials...");
            var inputs = new List<ObjectTextureAtlas.Input>();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(Encoding.UTF8.GetBytes($"atlas{Version}:{cellSize}:" + group.Key));
            foreach (var (key, candidate) in batch)
            {
                var m = candidate.Material; var slots = ShaderGuard.TextureSlots(m.Textures);
                hash.AppendData(Encoding.UTF8.GetBytes(key));
                foreach (string path in slots.Where((p, i) => i != 4 && p.Length > 0)) hash.AppendData(Encoding.UTF8.GetBytes(info[path].Hash));
                TextureImage Load(string path, int slot) => LodTextureEncoding.Limit(LodTextureEncoding.Decode(source.Read(path)), cellSize,
                    slot == 0 ? m.HasAlpha ? LodTextureEncoding.Channels.Color : LodTextureEncoding.Channels.OpaqueColor : slot == 1 ? LodTextureEncoding.Channels.Normal : LodTextureEncoding.Channels.Data,
                    LodTextureEncoding.IsSrgb(info[path].Info), slot == 0 ? ObjectTextureAssets.AlphaCutoff(m) : null);
                inputs.Add(new(key, Load(slots[0], 0), slots[1].Length > 0 ? Load(slots[1], 1) : null, m.HasAlpha, m.ClampMode,
                    ObjectTextureAssets.AlphaCutoff(m) ?? 128,
                    ExtraSlots.Where(i => slots[i].Length > 0).ToDictionary(i => i, i => Load(slots[i], i)),
                    slots.Select((p, i) => (p, i)).Where(t => t.i != 4 && t.p.Length > 0 && LodTextureEncoding.IsSrgb(info[t.p].Info)).Select(t => t.i).ToHashSet()));
            }
            var result = ObjectTextureAtlas.Build(inputs, cellSize, PageSize, ct);
            string stem = ObjectTextureAssets.Root(batch[0].Value.Material, "objects") + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()[..24];
            for (int page = 0; page < result.Pages.Count; page++)
            {
                string diffuse = stem + $"_{page}.dds", normal = stem + $"_{page}_n.dds";
                Write(diffuse, result.Pages[page].Diffuse); Write(normal, result.Pages[page].Normal); pages++;
                var extra = (result.Pages[page].Extra ?? new Dictionary<int, byte[]>()).ToDictionary(k => k.Key, k => stem + $"_{page}_s{k.Key}.dds");
                foreach (var (slot, path) in extra) Write(path, result.Pages[page].Extra![slot]);
                foreach (var (key, placement) in result.Placements.Where(p => p.Value.Page == page)) mapped[key] = new(diffuse, normal, placement.Rect, extra);
            }
        }
        var cache = new ConcurrentDictionary<LodMesh, LodMesh>(ReferenceEqualityComparer.Instance);
        LodMesh Transform(LodMesh mesh) => cache.GetOrAdd(mesh, m => Remap(m, mapped));
        int before = modelArray.SelectMany(m => m.Parts).Select(p => p.Material.Key).Distinct().Count();
        int after = modelArray.Select(Transform).SelectMany(m => m.Parts).Select(p => p.Material.Key).Distinct().Count();
        var report = new Report(mapped.Count, pages, eligible, skipped, reasons, before, after, textureBytes, warnings);
        File.WriteAllText(Path.Combine(output, "PenguLOD.atlas.json"), JsonSerializer.Serialize(report, PersistentPipeline.Json));
        var groupSizes = materials.Values.GroupBy(c => c.Group).Select(g => g.Count()).OrderDescending().ToArray();
        progress?.Report($"Object atlas: {mapped.Count:N0} materials on {pages} page sets; {before:N0} → {after:N0} distinct materials; {skipped:N0} parts retain original UVs/materials.");
        progress?.Report($"Object atlas: {groupSizes.Length:N0} groups of compatible materials; the largest hold {string.Join(", ", groupSizes.Take(8))} materials.");
        foreach (var warning in warnings.Take(20)) progress?.Report("Warning: object atlas kept source material: " + warning);
        // Keep content-addressed previous pages: out-of-scope and seasonal BTOs can still reference them.
        return new(report, Transform);

        void Write(string path, byte[] bytes) { ObjectTextureAssets.Write(output, path, bytes); textureBytes += bytes.Length; }
    }

    public static LodMesh Remap(LodMesh mesh, IReadOnlyDictionary<string, Mapping> mappings) => new()
    {
        Path = mesh.Path, Warnings = mesh.Warnings, NoShapesInFile = mesh.NoShapesInFile,
        Parts = mesh.Parts.Select(p =>
        {
            if (Ineligible(p) is not null || !mappings.TryGetValue(p.Material.Key, out var map) || TriangleShifts(p) is not { } shifts) return p;
            var textures = ShaderGuard.TextureSlots(p.Material.Textures); textures[0] = map.Diffuse; textures[1] = map.Normal;
            if (map.Extra is not null) foreach (var (slot, path) in map.Extra) textures[slot] = path;
            return Fold(p, p.Material with { Textures = textures, ClampMode = 0 }, shifts,
                v => new Vector2(map.Rect.U0 + v.X * (map.Rect.U1 - map.Rect.U0), map.Rect.V0 + v.Y * (map.Rect.V1 - map.Rect.V0)));
        }).ToArray()
    };
}
