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
    public const int Version = 6; // triangles that cross a tile edge are cut at it, unless that adds more than MaxCutTriangles
    /// <summary>Largest page side in pixels, the largest texture the texture code makes. At the default cell size a page holds up to 256 materials.</summary>
    public const int PageSize = 4096;
    public sealed record Report(int PackedMaterials, int Pages, int EligibleParts, int SkippedParts,
        IReadOnlyDictionary<string, int> Reasons, int SourceMaterials = 0, int AtlasMaterials = 0, long TextureBytes = 0,
        IReadOnlyList<string>? Warnings = null, int CutParts = 0, int CutTriangles = 0);
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
        if (Tiles(part) is not { } tiled || FoldedVertices(tiled.Part, tiled.Shifts) > ushort.MaxValue) return "tiling or invalid UVs";
        if (tiled.Part.TriangleCount - part.TriangleCount > MaxCutTriangles(part.TriangleCount)) return "cutting at tile edges adds too many triangles";
        return null;
    }

    /// <summary>Triangles that cutting at tile edges adds to a part; 0 when it needs no cut or its coordinates cannot be packed.</summary>
    public static int CutTriangles(LodMeshPart part) => Tiles(part) is { } tiled ? tiled.Part.TriangleCount - part.TriangleCount : 0;

    /// <summary>How far a triangle may reach past its tile, in tiles: the rounding of half-precision texture coordinates near a tile edge.</summary>
    private const float FoldTolerance = 0.002f;
    /// <summary>
    /// Most tiles one triangle may cover and still be cut. A triangle over more repeats than this is a surface the texture is
    /// tiled across many times; cutting it would turn one triangle into hundreds.
    /// </summary>
    public const int MaxTilesPerTriangle = 64;

    /// <summary>
    /// Most triangles cutting at tile edges may add to a part for it to be packed: a tenth of its own, or 64 for a small part. A part
    /// that needs more keeps its own texture. On a rock face or another surface the texture repeats over many times, cutting adds far
    /// more triangles than packing saves in draw calls.
    /// </summary>
    public static int MaxCutTriangles(int triangles) => Math.Max(64, triangles / 10);

    /// <summary>
    /// The part with every triangle inside one tile of its texture, and the whole-tile shift that brings each triangle's texture
    /// coordinates into the unit square. Null when the coordinates are unusable, a triangle leaves the unit square on an axis
    /// that clamps, or a triangle covers more than <see cref="MaxTilesPerTriangle"/> tiles.
    /// <para>Under wrapping, a triangle in tile (2, -1) samples exactly what it would in tile (0, 0), so each triangle can be
    /// moved on its own; a surface mapped across several tiles in separate pieces, as most LOD models of walls are, fits an atlas
    /// this way. A triangle that reaches across a tile edge, as on rock faces and anything else the texture repeats over, is cut
    /// along the edges first: the pieces lie in the plane of the triangle and carry interpolated normals and colours, so the
    /// surface is unchanged, and two triangles that share an edge are cut at the same points, so no gap opens between them.
    /// Triangles are moved whole, never single vertices: that would stretch a triangle across a tile edge.</para>
    /// </summary>
    private static (LodMeshPart Part, Vector2[] Shifts)? Tiles(LodMeshPart part)
    {
        var uvs = part.UVs; var triangles = part.Triangles;
        if (part.Material.ClampMode > 3 || uvs.Length != part.Positions.Length || uvs.Length == 0 || triangles.Length % 3 != 0) return null;
        bool wrapU = (part.Material.ClampMode & 2) != 0, wrapV = (part.Material.ClampMode & 1) != 0;
        var shifts = new List<Vector2>(triangles.Length / 3);
        Cutter? cutter = null;
        for (int t = 0; t < triangles.Length; t += 3)
        {
            int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
            if (a >= uvs.Length || b >= uvs.Length || c >= uvs.Length) return null;
            var min = Vector2.Min(uvs[a], Vector2.Min(uvs[b], uvs[c])); var max = Vector2.Max(uvs[a], Vector2.Max(uvs[b], uvs[c]));
            if (!float.IsFinite(min.X) || !float.IsFinite(min.Y) || !float.IsFinite(max.X) || !float.IsFinite(max.Y)) return null;
            var first = new Vector2(wrapU ? MathF.Floor(min.X + FoldTolerance) : 0, wrapV ? MathF.Floor(min.Y + FoldTolerance) : 0);
            if (min.X - first.X < -FoldTolerance || min.Y - first.Y < -FoldTolerance) return null;
            float tilesU = MathF.Max(1, MathF.Ceiling(max.X - FoldTolerance) - first.X), tilesV = MathF.Max(1, MathF.Ceiling(max.Y - FoldTolerance) - first.Y);
            if ((!wrapU && tilesU > 1) || (!wrapV && tilesV > 1) || tilesU * tilesV > MaxTilesPerTriangle) return null;
            if (tilesU == 1 && tilesV == 1)
            {
                shifts.Add(first); cutter?.Keep(a, b, c);
                continue;
            }
            cutter ??= new Cutter(part, t);
            cutter.Cut(a, b, c, first, (int)tilesU, (int)tilesV, shifts);
        }
        if (cutter is null) return (part, shifts.ToArray());
        return cutter.VertexCount > ushort.MaxValue ? null : (cutter.Build(), shifts.ToArray());
    }

    /// <summary>Cuts triangles of one part along the tile edges of its texture, adding the vertices the cuts need.</summary>
    private sealed class Cutter
    {
        private readonly LodMeshPart _part;
        private readonly List<Vector3> _positions, _normals, _tangents, _bitangents;
        private readonly List<Vector2> _uvs;
        private readonly List<uint>? _colors;
        private readonly List<int> _triangles;
        // One cut vertex per edge and tile line, whichever triangle asks first, so neighbours share it.
        private readonly Dictionary<(int Low, int High, int Axis, float Line), int> _cuts = [];
        private List<int> _polygon = [], _below = [], _above = [], _strip = [];

        /// <param name="part">The part being cut.</param>
        /// <param name="kept">How many of its triangle indices come before the first triangle that needs a cut; they are kept as they are.</param>
        public Cutter(LodMeshPart part, int kept)
        {
            _part = part;
            _positions = [.. part.Positions]; _uvs = [.. part.UVs]; _normals = [.. part.Normals]; _tangents = [.. part.Tangents];
            _bitangents = [.. part.Bitangents]; _colors = part.Colors is null ? null : [.. part.Colors];
            _triangles = new List<int>(part.Triangles.Length + 64);
            for (int i = 0; i < kept; i++) _triangles.Add(part.Triangles[i]);
        }

        public int VertexCount => _positions.Count;

        public void Keep(int a, int b, int c) { _triangles.Add(a); _triangles.Add(b); _triangles.Add(c); }

        /// <summary>Cuts triangle a, b, c into the tiles it covers, a column of tiles at a time, and adds one shift per piece.</summary>
        public void Cut(int a, int b, int c, Vector2 first, int tilesU, int tilesV, List<Vector2> shifts)
        {
            _polygon.Clear(); _polygon.Add(a); _polygon.Add(b); _polygon.Add(c);
            for (int u = 0; u < tilesU; u++)
            {
                if (u < tilesU - 1) { Split(_polygon, 0, first.X + u + 1); (_strip, _below) = (_below, _strip); (_polygon, _above) = (_above, _polygon); }
                else (_strip, _polygon) = (_polygon, _strip);
                for (int v = 0; v < tilesV; v++)
                {
                    List<int> piece;
                    if (v < tilesV - 1) { Split(_strip, 1, first.Y + v + 1); piece = _below; (_strip, _above) = (_above, _strip); }
                    else piece = _strip;
                    var shift = new Vector2(first.X + u, first.Y + v);
                    for (int i = 1; i + 1 < piece.Count; i++)           // a convex polygon, as a fan
                    {
                        int p = piece[0], q = piece[i], r = piece[i + 1];
                        if (p == q || q == r || p == r) continue;
                        Keep(p, q, r); shifts.Add(shift);
                    }
                }
            }
        }

        /// <summary>
        /// Splits a convex polygon at one tile line into the part below it and the part above it. A corner within the tolerance
        /// of the line belongs to both, so no sliver is cut off beside it.
        /// </summary>
        private void Split(List<int> polygon, int axis, float line)
        {
            _below.Clear(); _above.Clear();
            for (int i = 0; i < polygon.Count; i++)
            {
                int p = polygon[i], q = polygon[(i + 1) % polygon.Count];
                int sideP = Side(p, axis, line), sideQ = Side(q, axis, line);
                if (sideP <= 0) _below.Add(p);
                if (sideP >= 0) _above.Add(p);
                if (sideP * sideQ < 0) { int cut = CutVertex(p, q, axis, line); _below.Add(cut); _above.Add(cut); }
            }
        }

        private int Side(int vertex, int axis, float line)
        {
            float d = (axis == 0 ? _uvs[vertex].X : _uvs[vertex].Y) - line;
            return d < -FoldTolerance ? -1 : d > FoldTolerance ? 1 : 0;
        }

        private int CutVertex(int p, int q, int axis, float line)
        {
            var key = (Math.Min(p, q), Math.Max(p, q), axis, line);
            if (_cuts.TryGetValue(key, out int cut)) return cut;
            int low = key.Item1, high = key.Item2;      // always from the lower index, so both neighbours compute the same point
            float from = axis == 0 ? _uvs[low].X : _uvs[low].Y, to = axis == 0 ? _uvs[high].X : _uvs[high].Y;
            float t = (line - from) / (to - from);
            var uv = Vector2.Lerp(_uvs[low], _uvs[high], t);
            _uvs.Add(axis == 0 ? new Vector2(line, uv.Y) : new Vector2(uv.X, line));
            _positions.Add(Vector3.Lerp(_positions[low], _positions[high], t));
            _normals.Add(Direction(_normals[low], _normals[high], t)); _tangents.Add(Direction(_tangents[low], _tangents[high], t));
            _bitangents.Add(Direction(_bitangents[low], _bitangents[high], t));
            _colors?.Add(Color(_colors[low], _colors[high], t));
            return _cuts[key] = _positions.Count - 1;
        }

        private static Vector3 Direction(Vector3 a, Vector3 b, float t)
        {
            var mixed = Vector3.Lerp(a, b, t); float length = mixed.Length();
            return length > 1e-6f ? mixed / length : a;
        }

        private static uint Color(uint a, uint b, float t)
        {
            uint mixed = 0;
            for (int shift = 0; shift < 32; shift += 8)
            {
                float x = (a >> shift) & 0xFF, y = (b >> shift) & 0xFF;
                mixed |= (uint)Math.Clamp(MathF.Round(x + (y - x) * t), 0, 255) << shift;
            }
            return mixed;
        }

        public LodMeshPart Build() => _part with
        {
            Positions = [.. _positions], UVs = [.. _uvs], Normals = [.. _normals], Tangents = [.. _tangents], Bitangents = [.. _bitangents],
            Colors = _colors is null ? null : [.. _colors], Triangles = _triangles.Select(i => (ushort)i).ToArray(),
        };
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
        int eligible = 0, skipped = 0, cutParts = 0, cutTriangles = 0;
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
            if (reason is null)
            {
                eligible++;
                if (CutTriangles(part) is > 0 and var added) { cutParts++; cutTriangles += added; }
            }
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
        var report = new Report(mapped.Count, pages, eligible, skipped, reasons, before, after, textureBytes, warnings, cutParts, cutTriangles);
        File.WriteAllText(Path.Combine(output, "PenguLOD.atlas.json"), JsonSerializer.Serialize(report, PersistentPipeline.Json));
        var groupSizes = materials.Values.GroupBy(c => c.Group).Select(g => g.Count()).OrderDescending().ToArray();
        progress?.Report($"Object atlas: {mapped.Count:N0} materials on {pages} page sets; {before:N0} → {after:N0} distinct materials; {skipped:N0} parts retain original UVs/materials.");
        if (cutParts > 0) progress?.Report($"Object atlas: {cutParts:N0} model parts were cut at texture tile edges to fit, adding {cutTriangles:N0} triangles to those models.");
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
            if (!mappings.TryGetValue(p.Material.Key, out var map) || Ineligible(p) is not null || Tiles(p) is not { } tiled) return p;
            var textures = ShaderGuard.TextureSlots(p.Material.Textures); textures[0] = map.Diffuse; textures[1] = map.Normal;
            if (map.Extra is not null) foreach (var (slot, path) in map.Extra) textures[slot] = path;
            return Fold(tiled.Part, p.Material with { Textures = textures, ClampMode = 0 }, tiled.Shifts,
                v => new Vector2(map.Rect.U0 + v.X * (map.Rect.U1 - map.Rect.U0), map.Rect.V0 + v.Y * (map.Rect.V1 - map.Rect.V0)));
        }).ToArray()
    };
}
