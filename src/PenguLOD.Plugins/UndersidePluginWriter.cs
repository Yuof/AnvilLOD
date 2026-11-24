using System.Text.Json;
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
/// Writes the plugin that places the terrain underside, the way DynDOLOD does, as one ESL-flagged
/// <c>PenguLOD.esp</c> (no plugin slot): one moveable static per worldspace pointing at
/// <c>Terrain\&lt;ws&gt;\&lt;ws&gt;_Underside.nif</c>, and one persistent reference to it, 500 units below the worldspace origin, in the
/// worldspace's own persistent cell. It needs the worldspaces' plugins as masters, so it can't be ESM-flagged (an ESM must not
/// depend on plugins that aren't master-flagged); a plain ESP is all it needs. Earlier versions wrote a separate
/// <c>PenguLOD Underside.esm</c> and called the ESP <c>PenguLOD Underside.esp</c>; both are removed now.
/// The worldspace and its persistent cell are written as overrides of the winners at generation time; load the ESP after the
/// plugins that change those worldspaces and run Generate again whenever they change.
/// </summary>
public static class UndersidePluginWriter
{
    public const string FileName = "PenguLOD.esp";
    public const string FormIdsFileName = "PenguLOD.formids.json";
    /// <summary>Files older versions wrote (a separate ESM, then the ESP under its old name). No longer written; deleted when found.</summary>
    public static readonly string[] LegacyFileNames = ["PenguLOD Underside.esm", "PenguLOD Underside.esp"];

    /// <summary>How far below the world the underside sits. DynDOLOD uses the same value.</summary>
    public const float Depth = -500f;

    private static string UndersideModelPath(string worldspace) => PenguLOD.Meshes.UndersideBuilder.ModelPath(worldspace);

    /// <summary>FormIDs 0x800 to 0xFFF.</summary>
    public const int EslRecordLimit = 2048;

    private const int FlagNotPlayable = 0x4, FlagPersistent = 0x400, FlagInitiallyDisabled = 0x800, FlagIsFullLod = 0x10000; // the flags DynDOLOD sets

    public sealed record Result(string Path, IReadOnlyList<string> Worldspaces, IReadOnlyList<string> Skipped, IReadOnlyList<string> Masters,
        int NativeBases = 0, int NativeReferences = 0, IReadOnlyList<NativeGridObjectWriter.Entry>? NativeRefs = null, bool IsLight = true, string? EffectsPath = null, int EffectsPlugins = 0);

    /// <param name="worldspaces">EditorIDs of the worldspaces whose underside NIF was generated.</param>
    /// <param name="natives">Grid objects (waterfalls, windmill fans, wheels) to place as real references, see <see cref="NativeGridObjects"/>.</param>
    public const string EffectsFileName = "PenguLOD Effects.esp";
    /// <summary>New records an ESL can hold (FormIDs 0x800 to 0xFFF) minus a little spare room.</summary>
    public const int EslBudget = 2040;
    /// <summary>Same, for game 1.6.1130 and newer, which read ESLs with plugin header 1.71 and FormIDs 0x000 to 0xFFF.</summary>
    public const int LargeEslBudget = 4088;
    public const float LargeEslHeaderVersion = 1.71f;

    /// <summary>
    /// Splits the effects into as few plugins as fit: in order, each plugin takes references until its references plus the base records
    /// they need (one per distinct model and kind) would pass <see cref="EslBudget"/>. Cell overrides reuse the vanilla FormIDs, so they cost no new ones.
    /// </summary>
    public static IEnumerable<List<NativeGridObjects.Pick>> PackEffects(IReadOnlyList<NativeGridObjects.Pick> effects, bool large = false)
    {
        var chunk = new List<NativeGridObjects.Pick>();
        var bases = new HashSet<(string, bool)>();
        foreach (var p in effects)
        {
            int extra = bases.Contains((p.Model, p.Movable)) ? 1 : 2;
            if (chunk.Count + bases.Count + extra > (large ? LargeEslBudget : EslBudget) && chunk.Count > 0)
            {
                yield return chunk;
                chunk = [];
                bases.Clear();
            }
            chunk.Add(p);
            bases.Add((p.Model, p.Movable));
        }
        if (chunk.Count > 0) yield return chunk;
    }

    /// <summary>
    /// PenguLOD.esp (underside, waterfalls, windmills, wheels) and, when there are fires, candles, torches, lava or streams, a second
    /// plugin <c>PenguLOD Effects.esp</c> with those: each ESL-flagged while it fits (2,048 records), so neither takes a plugin slot.
    /// </summary>
    public static Result Write(GameContext game, IReadOnlyCollection<string> worldspaces, string outputFolder, IReadOnlyList<NativeGridObjects.Pick>? natives = null, bool largeEsl = false)
    {
        natives ??= [];
        var formIdMap = LoadFormIdMap(outputFolder);
        var main = WriteOne(game, FileName, worldspaces, outputFolder, natives.Where(n => !n.Effect).ToList(), towers: true, largeEsl: false, formIdMap: formIdMap);
        var effects = natives.Where(n => n.Effect).ToList();
        // Plugins from an earlier run ("PenguLOD Effects.esp", "PenguLOD Effects 2.esp", ...) are replaced.
        foreach (var old in Directory.Exists(outputFolder) ? Directory.GetFiles(outputFolder, "PenguLOD Effects*.esp") : [])
            File.Delete(old);
        if (effects.Count == 0)
        {
            SaveFormIdMap(outputFolder, formIdMap);
            return main;
        }
        // One ESL holds 2,048 new records: the references plus one base record per distinct model. Each plugin is filled up to that limit
        // (see PackEffects), so a list with many more fires and candles than ours only gets as many plugins as it really needs.
        var results = new List<Result>();
        foreach (var chunk in PackEffects(effects, largeEsl))
        {
            var name = results.Count == 0 ? EffectsFileName : $"PenguLOD Effects {results.Count + 1}.esp";
            results.Add(WriteOne(game, name, [], outputFolder, chunk, towers: false, largeEsl, formIdMap));
        }
        SaveFormIdMap(outputFolder, formIdMap);
        return main with
        {
            NativeBases = main.NativeBases + results.Sum(r => r.NativeBases),
            NativeReferences = main.NativeReferences + results.Sum(r => r.NativeReferences),
            NativeRefs = (main.NativeRefs ?? []).Concat(results.SelectMany(r => r.NativeRefs ?? [])).ToList(),
            Masters = main.Masters.Concat(results.SelectMany(r => r.Masters)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            IsLight = main.IsLight && results.All(r => r.IsLight),
            EffectsPath = results[0].Path,
            EffectsPlugins = results.Count,
        };
    }

    /// <summary>Editor IDs of the worldspaces that have a parent worldspace (the walled-city interiors-as-worlds: Whiterun, Riften, ...).</summary>
    public static HashSet<string> ChildWorldspaces(GameContext game) =>
        game.LoadOrder.PriorityOrder.Worldspace().WinningOverrides()
            .Where(w => w.Parent is { } p && !p.Worldspace.IsNull)
            .Select(w => w.EditorID).Where(e => e is not null).Select(e => e!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static Result WriteOne(GameContext game, string fileName, IReadOnlyCollection<string> worldspaces, string outputFolder,
        IReadOnlyList<NativeGridObjects.Pick> natives, bool towers, bool largeEsl = false,
        Dictionary<string, Dictionary<string, uint>>? formIdMap = null)
    {
        var undersideWorlds = new HashSet<string>(worldspaces, StringComparer.OrdinalIgnoreCase);
        var wanted = new HashSet<string>(worldspaces, StringComparer.OrdinalIgnoreCase);
        foreach (var n in natives) wanted.Add(n.Source.Ref.Worldspace);
        var nativeBase = new Dictionary<(string Model, bool Movable), FormKey>();
        var towerFix = new List<(string World, FormKey SourceRef, FormKey Base, Noggog.P3Float Pos, Noggog.P3Float Rot, float? Scale)>();
        if (towers)
        foreach (var pctx in game.LoadOrder.PriorityOrder.PlacedObject().WinningContextOverrides(game.LinkCache))
        {
            var pr = pctx.Record;
            if ((pr.MajorRecordFlagsRaw & (FlagPersistent | 0x8000 | FlagInitiallyDisabled)) != FlagPersistent || pr.Placement is not { } pp) continue;
            if (!pctx.TryGetParentSimpleContext<IWorldspaceGetter>(out var pws) || pws.Record.EditorID is not { } pwsId || !wanted.Contains(pwsId)) continue;
            if (!game.LinkCache.TryResolve<IStaticGetter>(pr.Base.FormKey, out var pst) || pst.EditorID?.StartsWith("SFarmhouseWindMill", StringComparison.OrdinalIgnoreCase) != true) continue;
            if ((pst.MajorRecordFlagsRaw & 0x8000) == 0) continue;
            towerFix.Add((pwsId, pr.FormKey, pr.Base.FormKey, pp.Position, pp.Rotation, pr.Scale));
        }
        int nativeRefs = 0, towerRefs = 0;
        var nativeEntries = new List<NativeGridObjectWriter.Entry>();
        var esp = new SkyrimMod(ModKey.FromNameAndExtension(fileName), SkyrimRelease.SkyrimSE) { IsSmallMaster = true };
        uint minId = largeEsl ? 0x001u : 0x800u;
        if (largeEsl) esp.ModHeader.Stats.Version = LargeEslHeaderVersion;
        esp.ModHeader.Stats.NextFormID = minId;

        Dictionary<string, uint> pluginIds = [];
        if (formIdMap is not null && !formIdMap.TryGetValue(fileName, out pluginIds!))
            formIdMap[fileName] = pluginIds = new(StringComparer.OrdinalIgnoreCase);
        var usedIds = new HashSet<uint>();
        foreach (var kv in pluginIds)
            if (kv.Value >= minId && kv.Value <= 0xFFFu) usedIds.Add(kv.Value);
        uint nextCursor = minId;

        FormKey Alloc(string roleKey)
        {
            if (pluginIds.TryGetValue(roleKey, out uint saved) && saved >= minId && (saved <= 0xFFFu || usedIds.Count >= (0xFFFu - minId + 1)))
            {
                usedIds.Add(saved);
                if (saved >= esp.ModHeader.Stats.NextFormID) esp.ModHeader.Stats.NextFormID = saved + 1;
                return new FormKey(esp.ModKey, saved);
            }
            while (usedIds.Contains(nextCursor)) nextCursor++;
            uint chosen = nextCursor++;
            usedIds.Add(chosen);
            pluginIds[roleKey] = chosen;
            if (chosen >= esp.ModHeader.Stats.NextFormID) esp.ModHeader.Stats.NextFormID = chosen + 1;
            return new FormKey(esp.ModKey, chosen);
        }

        var done = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ctx in game.LoadOrder.PriorityOrder.Worldspace().WinningContextOverrides())
        {
            var id = ctx.Record.EditorID;
            if (id is null || !wanted.Contains(id) || !seen.Add(id)) continue;

            MoveableStatic? stat = null;
            if (undersideWorlds.Contains(id))
            {
                stat = new MoveableStatic(Alloc("underside_base:" + id.ToLowerInvariant()), SkyrimRelease.SkyrimSE)
                { EditorID = $"PenguLOD_{id}_UndersideBase", MajorRecordFlagsRaw = FlagNotPlayable };
                esp.MoveableStatics.Add(stat);
                stat.Model = new Model { File = UndersideModelPath(id) };
                stat.ObjectBounds.First = new P3Int16(-4096, -4096, -4096);
                stat.ObjectBounds.Second = new P3Int16(4096, 4096, 4096);
            }

            ICell top;
            var topKey = ctx.Record.TopCell?.FormKey
                ?? game.LinkCache.ResolveAllContexts<IWorldspace, IWorldspaceGetter>(ctx.Record.FormKey)
                    .Select(c => c.Record.TopCell?.FormKey).FirstOrDefault(k => k is not null);
            if (topKey is { } key && game.LinkCache.TryResolveContext<ICell, ICellGetter>(key, out var cellCtx))
            {
                top = cellCtx.GetOrAddAsOverride(esp);
            }
            else
            {
                var ws = ctx.GetOrAddAsOverride(esp);
                top = ws.TopCell ??= new Cell(Alloc("topcell:" + id.ToLowerInvariant()), SkyrimRelease.SkyrimSE);
            }
            var carried = esp.Worldspaces.FirstOrDefault(w => w.FormKey == ctx.Record.FormKey);
            if (carried is not null) carried.MaxHeight = null;
            top.OcclusionData = null;
            top.MaxHeightData = null;
            top.Regions = null;
            top.Flags = 0;
            top.WaterHeight = null;
            top.Grid = null;
            top.Persistent.Clear();
            top.Temporary.Clear();
            if (stat is not null)
            {
                top.Persistent.Add(new PlacedObject(Alloc("underside_ref:" + id.ToLowerInvariant()), SkyrimRelease.SkyrimSE)
                {
                    EditorID = $"PenguLOD_{id}_Underside",
                    Base = new FormLinkNullable<IPlaceableObjectGetter>(stat.FormKey),
                    MajorRecordFlagsRaw = FlagPersistent | FlagIsFullLod,
                    Placement = new Placement { Position = new Noggog.P3Float(0, 0, Depth), Rotation = new Noggog.P3Float(0, 0, 0) },
                });
            }
            foreach (var n in natives.Where(n => n.Source.Ref.Worldspace.Equals(id, StringComparison.OrdinalIgnoreCase)))
            {
                if (!nativeBase.TryGetValue((n.Model, n.Movable), out var baseKey))
                {
                    var stem = System.IO.Path.GetFileNameWithoutExtension(n.Model.Replace('\\', '/'));
                    var edid = $"PenguLOD_{stem}_Base{(n.Movable ? "M" : "S")}";
                    short half = NativeGridObjects.IsAnimatedShip(n.Model.ToLowerInvariant()) ? (short)24000 : (short)2048;
                    var bounds = (new P3Int16((short)-half, (short)-half, (short)-half), new P3Int16(half, half, half));
                    string baseRole = $"native_base:{(n.Movable ? 'M' : 'S')}:{GamePath.Normalize(n.Model)}";
                    if (n.Movable)
                    {
                        var m = new MoveableStatic(Alloc(baseRole), SkyrimRelease.SkyrimSE) { EditorID = edid, MajorRecordFlagsRaw = FlagNotPlayable };
                        esp.MoveableStatics.Add(m);
                        m.Model = new Model { File = n.Model };
                        m.ObjectBounds.First = bounds.Item1; m.ObjectBounds.Second = bounds.Item2;
                        baseKey = m.FormKey;
                    }
                    else
                    {
                        var s = new Static(Alloc(baseRole), SkyrimRelease.SkyrimSE) { EditorID = edid, MajorRecordFlagsRaw = FlagNotPlayable };
                        esp.Statics.Add(s);
                        s.Model = new Model { File = n.Model };
                        s.ObjectBounds.First = bounds.Item1; s.ObjectBounds.Second = bounds.Item2;
                        baseKey = s.FormKey;
                    }
                    nativeBase[(n.Model, n.Movable)] = baseKey;
                }
                var r = n.Source.Ref;
                bool startDisabled = n.Effect || n.Source.InitiallyDisabled || n.Source.ParentPlugin is not null;
                string refRole = $"native_ref:{n.Source.RefPlugin.ToLowerInvariant()}:{n.Source.RefLocalId:x6}";
                var placed = new PlacedObject(Alloc(refRole), SkyrimRelease.SkyrimSE)
                {
                    EditorID = $"PenguLOD_{id}_Grid{++nativeRefs}",
                    Base = new FormLinkNullable<IPlaceableObjectGetter>(baseKey),
                    MajorRecordFlagsRaw = FlagPersistent | FlagIsFullLod | (startDisabled ? FlagInitiallyDisabled : 0),
                    Placement = new Placement
                    {
                        Position = new Noggog.P3Float(r.Position.X, r.Position.Y, r.Position.Z - (NativeGridObjects.IsWaterPlane(n.Model) ? NativeGridObjects.WaterDrop : 0f)),
                        Rotation = new Noggog.P3Float(r.RotationRadians.X, r.RotationRadians.Y, r.RotationRadians.Z),
                    },
                    Scale = r.Scale == 1f ? null : r.Scale,
                };
                top.Persistent.Add(placed);
                nativeEntries.Add(new NativeGridObjectWriter.Entry(n, placed.FormKey.ID, fileName));
            }
            foreach (var t in towerFix.Where(t => t.World.Equals(id, StringComparison.OrdinalIgnoreCase)))
            {
                string towerRole = $"tower_ref:{t.SourceRef.ModKey.FileName.String.ToLowerInvariant()}:{t.SourceRef.ID:x6}";
                top.Persistent.Add(new PlacedObject(Alloc(towerRole), SkyrimRelease.SkyrimSE)
                {
                    EditorID = $"PenguLOD_{id}_WindmillTower{++towerRefs}",
                    Base = new FormLinkNullable<IPlaceableObjectGetter>(t.Base),
                    MajorRecordFlagsRaw = FlagPersistent | 0x8000,
                    Placement = new Placement { Position = t.Pos, Rotation = t.Rot },
                    Scale = t.Scale,
                });
            }
            done.Add(id);
        }

        var skipped = wanted.Where(w => !seen.Contains(w)).OrderBy(w => w, StringComparer.OrdinalIgnoreCase).ToList();
        Directory.CreateDirectory(outputFolder);
        var espPath = Path.Combine(outputFolder, fileName);
        if (fileName == FileName)
        foreach (var old in LegacyFileNames)
        {
            var legacy = Path.Combine(outputFolder, old);
            if (File.Exists(legacy)) File.Delete(legacy);
        }
        // An ESL holds 2,048 records (FormIDs 0x800 to 0xFFF). Fires, candles and the rest of the effects go over that: then it is a plain ESP.
        // With the large range new FormIDs start at 0x001 (0x000 is unused), so 4,095 records fit.
        bool allowEsl = game.Release != Mutagen.Bethesda.GameRelease.SkyrimVR;
        bool small = allowEsl && esp.EnumerateMajorRecords().Count(r => r.FormKey.ModKey == esp.ModKey) <= (largeEsl ? 4095 : EslRecordLimit);
        esp.IsSmallMaster = small;
        var order = game.LoadOrder.ListedOrder.Select(l => l.ModKey).Where(k => k != esp.ModKey).ToList();
        esp.WriteToBinary(espPath, new BinaryWriteParameters
        {
            ModKey = ModKeyOption.CorrectToPath,
            MastersListOrdering = new MastersListOrderingByLoadOrder(order),
            RecordCount = RecordCountOption.Iterate,
            // FormIDs below 0x800 are valid in an ESL for game 1.6.1130 and newer (header 1.71); Mutagen refuses them unless told not to check.
            LowerRangeDisallowedHandler = largeEsl ? new NoCheckIfLowerRangeDisallowed() : new ThrowIfLowerRangeDisallowed(),
        });

        using var written = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE);
        var masters = written.ModHeader.MasterReferences.Select(m => m.Master.FileName.String).ToList();
        return new Result(espPath, done, skipped, masters, nativeBase.Count, nativeRefs, nativeEntries, small);
    }

    private static Dictionary<string, Dictionary<string, uint>> LoadFormIdMap(string outputFolder)
    {
        var map = new Dictionary<string, Dictionary<string, uint>>(StringComparer.OrdinalIgnoreCase);
        var file = Path.Combine(outputFolder, FormIdsFileName);
        if (!File.Exists(file)) return map;
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, uint>>>(File.ReadAllText(file));
            if (parsed is not null)
                foreach (var (plugin, entries) in parsed)
                    map[plugin] = new Dictionary<string, uint>(entries, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return map;
    }

    private static void SaveFormIdMap(string outputFolder, Dictionary<string, Dictionary<string, uint>> map)
    {
        if (map.Count == 0) return;
        try
        {
            Directory.CreateDirectory(outputFolder);
            var sorted = map.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(p => p.Key, p => p.Value.OrderBy(e => e.Value).ThenBy(e => e.Key, StringComparer.Ordinal).ToDictionary(e => e.Key, e => e.Value));
            var file = Path.Combine(outputFolder, FormIdsFileName);
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(sorted, PersistentPipeline.Json));
            File.Move(file + ".tmp", file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
