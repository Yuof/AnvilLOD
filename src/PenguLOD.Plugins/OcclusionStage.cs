using System.Diagnostics;
using PenguLOD.Core.Occlusion;
using PenguLOD.Core.Pipeline;
using PenguLOD.Core.World;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace PenguLOD.Plugins;

/// <summary>
/// Makes <c>Occlusion.esp</c> itself: for every big worldspace, a height grid (landscape, water, and the tops of the objects and trees that
/// are in LOD) goes to <see cref="OcclusionCalculator"/>, and the result is written as the TVDT and MHDT of the worldspace's exterior cells
/// (CELL overrides, plus bounds for statics that have none, see <see cref="BoundsFixStage"/>; ESL-flagged, load it last). It does what xLODGen's and DynDOLOD's occlusion run does, from data PenguLOD already has.
/// The bits are numbered for each worldspace's own LOD grid (see <see cref="TvdtData"/>): the game drops object LOD under any node it reads as hidden.
/// </summary>
public static class OcclusionStage
{
    public const string FileName = "Occlusion.esp";

    public sealed record Result(string? Path, int Worldspaces, int Cells, int Objects, int Trees, TimeSpan Elapsed, IReadOnlyList<string> Skipped,
        BoundsFixStage.Result? Bounds = null);

    /// <summary>Worldspaces whose LOD grid is smaller than this (cells per side) are left alone, like xLODGen does.</summary>
    public const int MinStride = 128;

    private const int PersistentFlag = 0x400;

    public static Result Run(GameContext game, TerrainHeights terrain, IReadOnlyDictionary<string, LodGrid> grids, IReadOnlyList<LodReference> refs,
        IReadOnlyList<TreeReference>? trees, IAssetSource assets, string folder, OcclusionSettings settings, IProgress<string>? progress,
        Action<string> warn, CancellationToken ct, IReadOnlySet<FormKey>? outdoorBases = null, OcclusionWorldRules? rules = null)
    {
        var sw = Stopwatch.StartNew();
        rules ??= OcclusionWorldRules.For(settings.Mode);
        var skipped = new List<string>();
        var children = UndersidePluginWriter.ChildWorldspaces(game);
        var worldspaces = game.LoadOrder.PriorityOrder.Worldspace().WinningOverrides()
            .Where(w => w.EditorID is not null)
            .ToDictionary(w => w.EditorID!, w => w, StringComparer.OrdinalIgnoreCase);
        // Child worldspaces that draw their parent's LOD (the walled cities) look at the same distant blocks as the parent's cell at
        // the same coordinates, so they get that cell's data, as DynDOLOD gives them.
        var names = worldspaces.Values.ToDictionary(w => w.FormKey, w => w.EditorID!);
        var lodChildren = worldspaces.Values
            .Where(w => w.Parent is { } p && p.Flags.HasFlag(WorldspaceParent.Flag.UseLodData) && names.ContainsKey(p.Worldspace.FormKey))
            .ToLookup(w => names[w.Parent!.Worldspace.FormKey], w => w.EditorID!, StringComparer.OrdinalIgnoreCase);
        var borderRegions = game.LoadOrder.PriorityOrder.Region().WinningOverrides()
            .Where(r => r.MajorFlags.HasFlag(Region.MajorFlag.BorderRegion)).Select(r => r.FormKey).ToHashSet();

        // The winning exterior cells of each worldspace. An Occlusion.esp from an earlier xLODGen, DynDOLOD or PenguLOD run would otherwise
        // be the winning override: its stale cell data would be copied forward over later cell edits by other mods, and every cell it
        // holds would count as one that already has occlusion data.
        bool allowEsl = game.Release != Mutagen.Bethesda.GameRelease.SkyrimVR || assets.Exists("skse\\plugins\\skyrimvresl.dll");
        var mod = new SkyrimMod(ModKey.FromNameAndExtension(FileName), SkyrimRelease.SkyrimSE) { IsSmallMaster = allowEsl };
        mod.ModHeader.Stats.NextFormID = 0x800;
        var cellsOf = new Dictionary<string, List<IModContext<ISkyrimMod, ISkyrimModGetter, ICell, ICellGetter>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var ctx in game.LoadOrder.PriorityOrder.Where(l => l.ModKey != mod.ModKey).Cell().WinningContextOverrides(game.LinkCache))
        {
            if (ctx.Record.Grid is null) continue;
            if (!ctx.TryGetParentSimpleContext<IWorldspaceGetter>(out var wsCtx) || wsCtx.Record.EditorID is not { } name) continue;
            // The worldspace's persistent cell holds persistent references; it is not a place. It carries the coordinates of
            // cell (0, 0) and would get that cell's data, and the plugin would override a record it has no reason to touch.
            if ((ctx.Record.MajorRecordFlagsRaw & PersistentFlag) != 0 || wsCtx.Record.TopCell?.FormKey == ctx.Record.FormKey) continue;
            if (!cellsOf.TryGetValue(name, out var list)) cellsOf[name] = list = [];
            list.Add(ctx);
        }

        var boundsCache = new Dictionary<string, (System.Numerics.Vector3 Min, System.Numerics.Vector3 Max)?>(StringComparer.OrdinalIgnoreCase);
        var meshCache = new Dictionary<string, PenguLOD.Meshes.LodMesh?>(StringComparer.OrdinalIgnoreCase);
        var activeMasters = new HashSet<ModKey>();
        int overMasterCells = 0;
        var overMasterPlugins = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        int worlds = 0, cellsTotal = 0, objectsTotal = 0, treesTotal = 0;

        foreach (var (ws, grid) in grids.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (children.Contains(ws) || !worldspaces.TryGetValue(ws, out var wrld) || grid.Settings.Stride < 16) { skipped.Add(ws); continue; }
            // Small worlds are left alone, as xLODGen (by the size of the LOD grid) and DynDOLOD (by the Small World flag) leave them,
            // unless the rules switch the worldspace on by name. The worlds that get no underside get no occlusion either, unless the
            // rules give them a mode of their own.
            bool? named = rules.Enabled(ws);
            if (named == false) { skipped.Add(ws); continue; }
            if (named != true && (grid.Settings.Stride < MinStride || wrld.Flags.HasFlag(Worldspace.Flag.SmallWorld)
                || (UndersideStage.IgnoredWorlds.Contains(ws) && !rules.Named(ws)))) { skipped.Add(ws); continue; }
            var landCells = terrain.CellsOf(ws).ToList();
            if (landCells.Count < 64) { skipped.Add(ws); continue; }

            // Which cells get data: the worldspace's own bounds (the playable area), then the mode.
            var mode = rules.ModeFor(ws);
            var own = cellsOf.GetValueOrDefault(ws) ?? [];
            var state = new Dictionary<(int X, int Y), (bool HasData, bool InBorder)>();
            foreach (var ctx in own)
                state[(ctx.Record.Grid!.Point.X, ctx.Record.Grid.Point.Y)] = (ctx.Record.OcclusionData is { Length: > 0 },
                    ctx.Record.Regions?.Any(r => borderRegions.Contains(r.FormKey)) == true);
            var bmin = wrld.ObjectBoundsMin; var bmax = wrld.ObjectBoundsMax;
            bool bounded = bmax.X > bmin.X && bmax.Y > bmin.Y;
            bool Flat(string world, int x, int y) => terrain.TryGetCell(world, x, y, out var v) && v.All(h => h == v[0]);
            var todo = landCells.Where(c => !bounded || (c.X >= (int)MathF.Floor(bmin.X) && c.X <= (int)MathF.Ceiling(bmax.X) && c.Y >= (int)MathF.Floor(bmin.Y) && c.Y <= (int)MathF.Ceiling(bmax.Y)))
                .Where(c => state.TryGetValue((c.X, c.Y), out var s) && mode.Includes(s.HasData, Flat(ws, c.X, c.Y), s.InBorder))
                .Select(c => (c.X, c.Y)).ToHashSet();

            int x0 = landCells.Min(c => c.X), x1 = landCells.Max(c => c.X), y0 = landCells.Min(c => c.Y), y1 = landCells.Max(c => c.Y);
            // A child worldspace's cells are not in the parent's border region; the mode applies to them without "+Border".
            var childMode = mode with { BorderOnly = false };
            var childCells = new List<IModContext<ISkyrimMod, ISkyrimModGetter, ICell, ICellGetter>>();
            foreach (string child in lodChildren[ws])
            {
                if (rules.Enabled(child) == false) continue;
                foreach (var ctx in cellsOf.GetValueOrDefault(child) ?? [])
                {
                    var p = ctx.Record.Grid!.Point;
                    if (p.X < x0 || p.X > x1 || p.Y < y0 || p.Y > y1 || !terrain.TryGetCell(ws, p.X, p.Y, out _)) continue;
                    if (childMode.Includes(ctx.Record.OcclusionData is { Length: > 0 }, Flat(child, p.X, p.Y), false)) childCells.Add(ctx);
                }
            }
            if (todo.Count == 0 && childCells.Count == 0) { skipped.Add(ws); continue; }

            float seaLevel = wrld.LandDefaults?.DefaultWaterHeight is { } wh && float.IsFinite(wh) ? wh : -14000f;
            var bareHeights = new OcclusionGrid(x0, y0, x1 - x0 + 1, y1 - y0 + 1, seaLevel);
            var heights = new OcclusionGrid(x0, y0, x1 - x0 + 1, y1 - y0 + 1, seaLevel);
            foreach (var c in landCells)
            {
                if (terrain.TryGetCell(ws, c.X, c.Y, out var v))
                {
                    bareHeights.SetTerrain(c.X, c.Y, v);
                    heights.SetTerrain(c.X, c.Y, v);
                }
                if (terrain.WaterAt(ws, c.X * 4096f + 2048f, c.Y * 4096f + 2048f) is { } water)
                    heights.RaiseTop(c.X * 4096f, c.Y * 4096f, (c.X + 1) * 4096f - 1f, (c.Y + 1) * 4096f - 1f, water);
            }

            // The tops of what stands in LOD: objects by their LOD mesh, trees by their billboard.
            // Large opaque static LOD4 meshes (mountains, cliffs >= 2048u footprint) also stamp conservative occluder heights into heights.Terrain.
            int objects = 0, treeCount = 0, mountainOccluders = 0;
            var owned = new List<(float X, float Y, float Radius, float Top)>();
            foreach (var r in refs)
            {
                if (!string.Equals(r.Worldspace, ws, StringComparison.OrdinalIgnoreCase)) continue;
                var mesh = r.Meshes.Lod4 ?? r.Meshes.Lod8 ?? r.Meshes.Lod16 ?? r.Meshes.Lod32;
                if (string.IsNullOrEmpty(mesh)) continue;
                var b = MeshBounds(assets, mesh, boundsCache, meshCache);
                if (b is null) continue;
                float scale = r.Scale <= 0f ? 1f : r.Scale;
                var (minX, minY, maxX, maxY, topOffset) = RotatedBounds(b.Value.Min, b.Value.Max, r.RotationRadians, scale);
                float radius = MathF.Max(MathF.Max(MathF.Abs(minX), MathF.Abs(maxX)), MathF.Max(MathF.Abs(minY), MathF.Abs(maxY)));
                float top = r.Position.Z + topOffset;
                heights.RaiseTop(r.Position.X + minX, r.Position.Y + minY, r.Position.X + maxX, r.Position.Y + maxY, top);
                if (r.Meshes.Lod4 is { Length: > 0 } lod4 && !lod4.StartsWith('~')
                    && maxX - minX >= 2048f && maxY - minY >= 2048f
                    && StampOpaqueMountain(heights, assets, lod4, r, scale, meshCache, verticalInset: 128f))
                {
                    mountainOccluders++;
                }
                owned.Add((r.Position.X, r.Position.Y, radius, top));
                objects++;
            }
            if (trees is not null)
                foreach (var t in trees)
                {
                    if (!string.Equals(t.Worldspace, ws, StringComparison.OrdinalIgnoreCase)) continue;
                    float half = Math.Max(t.Billboard.Width * 0.5f, 64f);
                    heights.RaiseTop(t.Position.X - half, t.Position.Y - half, t.Position.X + half, t.Position.Y + half,
                        t.Position.Z + Math.Max(t.Billboard.Height, t.Billboard.Height + t.Billboard.ShiftZ));
                    treeCount++;
                }

            var compute = todo.Concat(childCells.Select(c => (c.Record.Grid!.Point.X, c.Record.Grid.Point.Y))).Distinct().ToList();
            progress?.Report($"Occlusion {ws}: {compute.Count:N0} cells (mode {mode}), {objects:N0} objects ({mountainOccluders:N0} mountain occluders) and {treeCount:N0} trees in the height grid ({heights.Width}x{heights.Height} samples)");
            var calc = new OcclusionCalculator(heights, grid.Settings.SwCellX, grid.Settings.SwCellY, settings, grid.Settings.Stride);
            calc.AddOwnedTargets(owned);
            int last = 0;
            var data = calc.ComputeMany(compute, new Progress<int>(n => { if (n - last >= 2000) { last = n; progress?.Report($"Occlusion {ws}: {n:N0} of {compute.Count:N0} cells"); } }), ct);

            int written = 0;
            var wsMastersCache = new Dictionary<(ModKey Mod, FormKey Form), HashSet<ModKey>>();
            foreach (var ctx in own.Where(c => todo.Contains((c.Record.Grid!.Point.X, c.Record.Grid.Point.Y))).Concat(childCells))
            {
                var g = ctx.Record.Grid!;
                if (!data.TryGetValue((g.Point.X, g.Point.Y), out var tvdt)) continue;
                var cellMasters = BoundsFixStage.Masters(ctx.Record, mod.ModKey);
                if (ctx.TryGetParentSimpleContext<IWorldspaceGetter>(out var cellWsCtx))
                {
                    var wsKey = (cellWsCtx.ModKey, cellWsCtx.Record.FormKey);
                    if (!wsMastersCache.TryGetValue(wsKey, out var wsMasters))
                        wsMastersCache[wsKey] = wsMasters = BoundsFixStage.Masters(cellWsCtx.Record, mod.ModKey);
                    cellMasters.UnionWith(wsMasters);
                }
                var addedMasters = cellMasters.Where(m => !activeMasters.Contains(m)).ToList();
                if (activeMasters.Count + addedMasters.Count > BoundsFixStage.MaxMasters)
                {
                    overMasterCells++;
                    overMasterPlugins.Add(ctx.ModKey.FileName.String);
                    continue;
                }
                foreach (var m in addedMasters) activeMasters.Add(m);
                var cell = ctx.GetOrAddAsOverride(mod);
                // Keep loaded-cell MaxHeightData computed strictly from bare LAND (never raised by mountain meshes) so caves/arches inside cells aren't culled.
                if (cell.MaxHeightData is null)
                {
                    var (offset, steps) = OcclusionCalculator.BuildMaxHeight(bareHeights, g.Point.X, g.Point.Y);
                    var map = new Array2d<byte>(new byte[32, 32]);
                    for (int i = 0; i < 32; i++)
                        for (int j = 0; j < 32; j++) map[j, i] = steps[i * 32 + j];
                    cell.MaxHeightData = new CellMaxHeightData { Offset = offset, HeightMap = map };
                }
                cell.OcclusionData = tvdt.ToBytes();
                written++;
            }
            progress?.Report($"Occlusion {ws}: {written:N0} cells written" + (childCells.Count > 0 ? $", {childCells.Count:N0} of them in child worldspaces" : ""));
            worlds++; cellsTotal += written; objectsTotal += objects; treesTotal += treeCount;
        }

        if (overMasterCells > 0)
            warn($"Occlusion: {overMasterCells:N0} exterior cells were left without new occlusion overrides because their winning plugins would take {FileName} over {BoundsFixStage.MaxMasters} masters. Plugins: {string.Join(", ", overMasterPlugins.Take(12))}"
                 + (overMasterPlugins.Count > 12 ? $" and {overMasterPlugins.Count - 12} more." : "."));

        // Statics with empty bounds are culled by the occlusion data in loaded cells (Base Object Swapper replacements, hand-made statics).
        BoundsFixStage.Result? bounds = null;
        if (cellsTotal > 0)
        {
            bounds = BoundsFixStage.Run(game, assets, mod, outdoorBases, progress, ct);
            if (bounds.OverMasterLimit > 0)
                warn($"Occlusion: {bounds.OverMasterLimit:N0} statics with empty bounds were not given bounds, because their plugins would take {FileName} over "
                     + $"{BoundsFixStage.MaxMasters} masters. They may not show in loaded cells while {FileName} is active. Plugins: {string.Join(", ", bounds.LeftOutPlugins!.Take(12))}"
                     + (bounds.LeftOutPlugins!.Count > 12 ? $" and {bounds.LeftOutPlugins.Count - 12} more." : "."));
        }

        if (cellsTotal == 0)
        {
            warn("Occlusion: no worldspace was big enough or had landscape, so no Occlusion.esp was made.");
            return new Result(null, 0, 0, 0, 0, sw.Elapsed, skipped);
        }
        Directory.CreateDirectory(folder);
        var path = System.IO.Path.Combine(folder, FileName);
        var order = game.LoadOrder.ListedOrder.Select(l => l.ModKey).Where(k => k != mod.ModKey).ToList();
        mod.WriteToBinary(path, new BinaryWriteParameters
        {
            ModKey = ModKeyOption.CorrectToPath,
            MastersListOrdering = new MastersListOrderingByLoadOrder(order),
            RecordCount = RecordCountOption.Iterate,
        });
        return new Result(path, worlds, cellsTotal, objectsTotal, treesTotal, sw.Elapsed, skipped, bounds);
    }

    /// <summary>World-axis bounds relative to the reference origin after applying 3D rotation and scale.</summary>
    internal static (float MinX, float MinY, float MaxX, float MaxY, float MaxZ) RotatedBounds(
        System.Numerics.Vector3 lo, System.Numerics.Vector3 hi, System.Numerics.Vector3 rotationRadians, float scale)
    {
        if (MathF.Abs(rotationRadians.X) < 1e-6f && MathF.Abs(rotationRadians.Y) < 1e-6f && MathF.Abs(rotationRadians.Z) < 1e-6f)
            return (lo.X * scale, lo.Y * scale, hi.X * scale, hi.Y * scale, hi.Z * scale);
        var mat = PenguLOD.Meshes.Transforms.Reference(System.Numerics.Vector3.Zero, rotationRadians, scale);
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        for (int mask = 0; mask < 8; mask++)
        {
            var corner = new System.Numerics.Vector3((mask & 1) == 0 ? lo.X : hi.X, (mask & 2) == 0 ? lo.Y : hi.Y, (mask & 4) == 0 ? lo.Z : hi.Z);
            var w = System.Numerics.Vector3.Transform(corner, mat);
            minX = MathF.Min(minX, w.X); maxX = MathF.Max(maxX, w.X);
            minY = MathF.Min(minY, w.Y); maxY = MathF.Max(maxY, w.Y);
            maxZ = MathF.Max(maxZ, w.Z);
        }
        return (minX, minY, maxX, maxY, maxZ);
    }

    /// <summary>A LOD mesh's local-space bounding box, cached by path. Null when it can't be read.</summary>
    private static (System.Numerics.Vector3 Min, System.Numerics.Vector3 Max)? MeshBounds(
        IAssetSource assets, string path,
        Dictionary<string, (System.Numerics.Vector3 Min, System.Numerics.Vector3 Max)?> cache,
        Dictionary<string, PenguLOD.Meshes.LodMesh?> meshCache)
    {
        if (cache.TryGetValue(path, out var known)) return known;
        (System.Numerics.Vector3 Min, System.Numerics.Vector3 Max)? result = null;
        if (LoadMesh(assets, path, meshCache) is { } mesh)
        {
            var lo = new System.Numerics.Vector3(float.MaxValue);
            var hi = new System.Numerics.Vector3(float.MinValue);
            bool any = false;
            foreach (var part in mesh.Parts)
                foreach (var p in part.Positions)
                {
                    lo = System.Numerics.Vector3.Min(lo, p);
                    hi = System.Numerics.Vector3.Max(hi, p);
                    any = true;
                }
            if (any) result = (lo, hi);
        }
        cache[path] = result;
        return result;
    }

    private static PenguLOD.Meshes.LodMesh? LoadMesh(IAssetSource assets, string path, Dictionary<string, PenguLOD.Meshes.LodMesh?> meshCache)
    {
        if (meshCache.TryGetValue(path, out var cached)) return cached;
        PenguLOD.Meshes.LodMesh? mesh = null;
        try
        {
            var rel = path.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase) ? path : "meshes\\" + path;
            if (assets.TryOpen(rel, out var stream))
            {
                byte[] bytes;
                using (stream) { using var ms = new MemoryStream(); stream.CopyTo(ms); bytes = ms.ToArray(); }
                mesh = PenguLOD.Meshes.Nif.NifGeometryReader.Read(rel, bytes);
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException or NotSupportedException or IOException) { }
        meshCache[path] = mesh;
        return mesh;
    }

    /// <summary>
    /// Rasterizes upward-facing solid opaque triangles of a large LOD4 mesh onto <paramref name="grid"/>'s <c>Terrain</c>
    /// at 128-unit sample centers, recessed by <paramref name="verticalInset"/> so silhouette edges never over-occlude.
    /// </summary>
    internal static bool StampOpaqueMountain(OcclusionGrid grid, IAssetSource assets, string lod4Path, LodReference r, float scale,
        Dictionary<string, PenguLOD.Meshes.LodMesh?> meshCache, float verticalInset = 128f)
    {
        if (LoadMesh(assets, lod4Path, meshCache) is not { } mesh) return false;
        var xf = PenguLOD.Meshes.Transforms.Reference(r.Position, r.RotationRadians, scale);
        float originX = grid.CellX0 * 4096f, originY = grid.CellY0 * 4096f;
        bool anyRaised = false;

        foreach (var part in mesh.Parts)
        {
            if (part.Material.HasAlpha) continue;
            var pos = part.Positions;
            var tris = part.Triangles;
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                var a = System.Numerics.Vector3.Transform(pos[tris[t]], xf);
                var b = System.Numerics.Vector3.Transform(pos[tris[t + 1]], xf);
                var c = System.Numerics.Vector3.Transform(pos[tris[t + 2]], xf);
                var n = System.Numerics.Vector3.Cross(b - a, c - a);
                // Only stamp upward-facing solid rock surfaces (n.Z > 0 in XY area = counter-clockwise upward); also accept flipped winding if substantial XY area.
                float area2 = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                if (MathF.Abs(area2) < 256f) continue;
                float nLen = n.Length();
                if (nLen < 1e-3f || MathF.Abs(n.Z) / nLen < 0.15f) continue;

                float minX = MathF.Min(a.X, MathF.Min(b.X, c.X)), maxX = MathF.Max(a.X, MathF.Max(b.X, c.X));
                float minY = MathF.Min(a.Y, MathF.Min(b.Y, c.Y)), maxY = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
                int gx0 = Math.Max(0, (int)MathF.Floor((minX - originX) / OcclusionGrid.SampleSize));
                int gx1 = Math.Min(grid.Width - 1, (int)MathF.Floor((maxX - originX) / OcclusionGrid.SampleSize));
                int gy0 = Math.Max(0, (int)MathF.Floor((minY - originY) / OcclusionGrid.SampleSize));
                int gy1 = Math.Min(grid.Height - 1, (int)MathF.Floor((maxY - originY) / OcclusionGrid.SampleSize));
                float invArea = 1f / area2;

                for (int gy = gy0; gy <= gy1; gy++)
                {
                    float py = originY + (gy + 0.5f) * OcclusionGrid.SampleSize;
                    for (int gx = gx0; gx <= gx1; gx++)
                    {
                        float px = originX + (gx + 0.5f) * OcclusionGrid.SampleSize;
                        float w0 = ((b.X - px) * (c.Y - py) - (b.Y - py) * (c.X - px)) * invArea;
                        float w1 = ((c.X - px) * (a.Y - py) - (c.Y - py) * (a.X - px)) * invArea;
                        float w2 = 1f - w0 - w1;
                        if (w0 < 0f || w1 < 0f || w2 < 0f) continue;
                        float hz = w0 * a.Z + w1 * b.Z + w2 * c.Z - verticalInset;
                        if (grid.RaiseTerrainSample(gx, gy, hz)) anyRaised = true;
                    }
                }
            }
        }
        return anyRaised;
    }

    /// <summary>
    /// Exports mountain-stamped Community Shaders <c>.Terrain.HeightMap.</c> DDS files into <paramref name="lodOutputFolder"/>
    /// for worldspaces that already have a terrain heightmap installed and contain mountain occluders taller than bare LAND.
    /// </summary>
    public static int ExportMountainHeightmaps(GameContext game, TerrainHeights terrain, IReadOnlyDictionary<string, LodGrid> grids,
        IReadOnlyList<LodReference> refs, IAssetSource assets, string lodOutputFolder, IProgress<string>? progress, Action<string> warn, CancellationToken ct)
    {
        var boundsCache = new Dictionary<string, (System.Numerics.Vector3 Min, System.Numerics.Vector3 Max)?>(StringComparer.OrdinalIgnoreCase);
        var meshCache = new Dictionary<string, PenguLOD.Meshes.LodMesh?>(StringComparer.OrdinalIgnoreCase);
        var worldspaces = game.LoadOrder.PriorityOrder.Worldspace().WinningOverrides()
            .Where(w => w.EditorID is not null).ToDictionary(w => w.EditorID!, w => w, StringComparer.OrdinalIgnoreCase);
        int exported = 0;

        foreach (var (ws, _) in grids.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            string prefix = $"textures\\terrain\\{ws.ToLowerInvariant()}\\";
            var existingMap = (assets as AssetIndex)?.EnumeratePaths(prefix).FirstOrDefault(p => p.Contains(".terrain.heightmap.", StringComparison.OrdinalIgnoreCase));
            if (existingMap is null) continue;

            var landCells = terrain.CellsOf(ws).ToList();
            if (landCells.Count == 0) continue;
            int x0 = landCells.Min(c => c.X), x1 = landCells.Max(c => c.X), y0 = landCells.Min(c => c.Y), y1 = landCells.Max(c => c.Y);
            float defaultLand = worldspaces.TryGetValue(ws, out var wrld) && wrld.LandDefaults?.DefaultLandHeight is { } dl && float.IsFinite(dl) ? dl : -2048f;
            var grid = new OcclusionGrid(x0, y0, x1 - x0 + 1, y1 - y0 + 1, defaultLand);
            foreach (var c in landCells)
                if (terrain.TryGetCell(ws, c.X, c.Y, out var v)) grid.SetTerrain(c.X, c.Y, v);

            int stamped = 0;
            foreach (var r in refs)
            {
                if (!string.Equals(r.Worldspace, ws, StringComparison.OrdinalIgnoreCase)) continue;
                if (r.Meshes.Lod4 is not { Length: > 0 } lod4 || lod4.StartsWith('~')) continue;
                var b = MeshBounds(assets, lod4, boundsCache, meshCache);
                if (b is null) continue;
                float scale = r.Scale <= 0f ? 1f : r.Scale;
                var (minX, minY, maxX, maxY, _) = RotatedBounds(b.Value.Min, b.Value.Max, r.RotationRadians, scale);
                if (maxX - minX < 2048f || maxY - minY < 2048f) continue;
                if (StampOpaqueMountain(grid, assets, lod4, r, scale, meshCache, verticalInset: 0f))
                    stamped++;
            }
            if (stamped == 0) continue;

            float Sample(float wx, float wy)
            {
                float? bare = terrain.HeightAt(ws, wx, wy);
                int gx = (int)MathF.Floor((wx - x0 * 4096f) / OcclusionGrid.SampleSize);
                int gy = (int)MathF.Floor((wy - y0 * 4096f) / OcclusionGrid.SampleSize);
                float mtn = grid.TerrainAt(gx, gy);
                return bare is { } bh ? MathF.Max(bh, mtn) : mtn;
            }

            try
            {
                var hm = TerrainHeightmap.Export(ws, x0, y0, x1, y1, 8192, (wx, wy) => Sample(wx, wy), ct, fallbackHeight: defaultLand, warn: warn);
                string full = GamePath.ToFileSystemPath(lodOutputFolder, hm.Path);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, hm.Bytes);
                exported++;
                progress?.Report($"Mountain heightmap {ws}: stamped {stamped:N0} mountain/cliff models into {hm.Path} for Community Shaders Terrain Shadows.");
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            {
                warn($"{ws}: skipped mountain-stamped heightmap export ({ex.Message}).");
            }
        }
        return exported;
    }
}
