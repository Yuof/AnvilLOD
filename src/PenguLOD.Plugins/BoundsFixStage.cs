using System.Numerics;
using PenguLOD.Core.Pipeline;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace PenguLOD.Plugins;

/// <summary>
/// Gives static records that have empty object bounds (0,0,0 to 0,0,0) the bounds of their model, as overrides inside Occlusion.esp.
/// With occlusion data in the cells the engine culls an object in a loaded cell by its base record's bounds. A record without any is a
/// point on the ground, below the cell's max-height surface, so the object is never drawn. Hand-made statics and Base Object Swapper
/// replacements are the usual ones (AnvilLOD 0.2.75: whole farmhouses missing at Falkreath and Pinewatch). The base game's and Creation
/// Club's own records are left alone: they work as shipped.
/// <para>
/// Only statics that can stand outdoors are given bounds: those placed in an exterior cell, and the replacements Base Object Swapper puts
/// in. A large load order has thousands of statics with empty bounds, most of them never placed outside, and every plugin they come from
/// would become a master of Occlusion.esp. A plugin can have at most <see cref="MaxMasters"/>; records that would need more are left
/// alone and reported.
/// </para>
/// </summary>
public static class BoundsFixStage
{
    /// <summary>A model smaller than this on every axis is left alone (markers, tiny clutter).</summary>
    public const float MinExtent = 24f;

    /// <summary>Masters Occlusion.esp may have, kept a little under the format's 254 so the write can never fail.</summary>
    public const int MaxMasters = 250;

    private static readonly HashSet<string> BaseGame = new(StringComparer.OrdinalIgnoreCase)
        { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" };

    public static bool IsBaseGameOrCreationClub(ModKey key) =>
        BaseGame.Contains(key.FileName.String) || key.FileName.String.StartsWith("cc", StringComparison.OrdinalIgnoreCase);

    /// <param name="Checked">Statics with empty bounds that can stand outdoors.</param>
    /// <param name="Fixed">Of those, given their model's bounds.</param>
    /// <param name="NoModel">Without a readable model.</param>
    /// <param name="TooSmall">With a model smaller than <see cref="MinExtent"/> on every axis.</param>
    /// <param name="OverMasterLimit">Left alone because they would take Occlusion.esp over <see cref="MaxMasters"/> masters.</param>
    /// <param name="LeftOutPlugins">The plugins those come from.</param>
    public sealed record Result(int Checked, int Fixed, int NoModel, int TooSmall, int OverMasterLimit = 0, IReadOnlyList<string>? LeftOutPlugins = null);

    /// <param name="mod">Occlusion.esp being built; its records so far decide which masters are already needed.</param>
    /// <param name="outdoor">Base records placed in exterior cells or put in by Base Object Swapper; null treats every static as one.</param>
    public static Result Run(GameContext game, IAssetSource assets, SkyrimMod mod, IReadOnlySet<FormKey>? outdoor, IProgress<string>? progress, CancellationToken ct)
    {
        var cache = new Dictionary<string, (Vector3 Min, Vector3 Max)?>(StringComparer.OrdinalIgnoreCase);
        int total = 0, noModel = 0, tooSmall = 0;
        var candidates = new List<(IModContext<ISkyrimMod, ISkyrimModGetter, IStatic, IStaticGetter> Context, Vector3 Min, Vector3 Max, HashSet<ModKey> Masters)>();
        // An Occlusion.esp from an earlier run is in the load order under the same name; its bounds must not count as already fixed.
        foreach (var ctx in game.LoadOrder.PriorityOrder.Where(l => l.ModKey != mod.ModKey).Static().WinningContextOverrides())
        {
            ct.ThrowIfCancellationRequested();
            var rec = ctx.Record;
            if (!IsEmpty(rec.ObjectBounds) || IsBaseGameOrCreationClub(rec.FormKey.ModKey)) continue;
            if (outdoor is not null && !outdoor.Contains(rec.FormKey)) continue;
            total++;
            var file = rec.Model?.File;
            if (string.IsNullOrWhiteSpace(file) || ModelBounds(assets, file, cache) is not { } b) { noModel++; continue; }
            var (min, max) = b;
            if (max.X - min.X < MinExtent && max.Y - min.Y < MinExtent && max.Z - min.Z < MinExtent) { tooSmall++; continue; }
            candidates.Add((ctx, min, max, Masters(rec, mod.ModKey)));
        }

        // Masters Occlusion.esp needs already (its cell overrides), then the records that add none, then those whose plugins bring the most
        // records per new master, until the limit.
        var masters = new HashSet<ModKey>();
        foreach (var record in mod.EnumerateMajorRecords()) masters.UnionWith(Masters(record, mod.ModKey));
        int fixedCount = 0, over = 0;
        var leftOut = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in candidates.GroupBy(c => string.Join('|', c.Masters.Where(m => !masters.Contains(m)).Select(m => m.FileName.String).Order(StringComparer.OrdinalIgnoreCase)))
                     .OrderBy(g => g.Key.Length == 0 ? 0 : 1).ThenByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var added = group.SelectMany(c => c.Masters).Where(m => !masters.Contains(m)).ToHashSet();
            if (masters.Count + added.Count > MaxMasters)
            {
                over += group.Count();
                foreach (var c in group) leftOut.Add(c.Context.Record.FormKey.ModKey.FileName.String);
                continue;
            }
            masters.UnionWith(added);
            foreach (var (ctx, min, max, _) in group.OrderBy(c => c.Context.Record.FormKey.ToString(), StringComparer.Ordinal))
            {
                var s = ctx.GetOrAddAsOverride(mod);
                s.ObjectBounds.First = ToShort(min, floor: true);
                s.ObjectBounds.Second = ToShort(max, floor: false);
                fixedCount++;
            }
        }
        progress?.Report($"Occlusion: gave {fixedCount:N0} of {total:N0} statics that stand outdoors and have empty bounds their model's bounds"
                         + $" ({noModel:N0} had no readable model, {tooSmall:N0} are tiny).");
        return new Result(total, fixedCount, noModel, tooSmall, over, leftOut.ToList());
    }

    /// <summary>The plugins a copy of the record needs as masters: the one that defines it and every one it links to.</summary>
    private static HashSet<ModKey> Masters(IMajorRecordGetter record, ModKey self)
    {
        var keys = new HashSet<ModKey> { record.FormKey.ModKey };
        foreach (var link in record.EnumerateFormLinks()) if (!link.IsNull) keys.Add(link.FormKey.ModKey);
        keys.Remove(self);
        return keys;
    }

    private static bool IsEmpty(IObjectBoundsGetter b) =>
        b.First.X == 0 && b.First.Y == 0 && b.First.Z == 0 && b.Second.X == 0 && b.Second.Y == 0 && b.Second.Z == 0;

    private static P3Int16 ToShort(Vector3 v, bool floor)
    {
        static short C(float f) => (short)Math.Clamp((int)f, short.MinValue, short.MaxValue);
        return floor
            ? new P3Int16(C(MathF.Floor(v.X)), C(MathF.Floor(v.Y)), C(MathF.Floor(v.Z)))
            : new P3Int16(C(MathF.Ceiling(v.X)), C(MathF.Ceiling(v.Y)), C(MathF.Ceiling(v.Z)));
    }

    /// <summary>The box around every vertex of a model, in model space. Null when the model is missing, unreadable or has no geometry.</summary>
    private static (Vector3 Min, Vector3 Max)? ModelBounds(IAssetSource assets, string path, Dictionary<string, (Vector3 Min, Vector3 Max)?> cache)
    {
        if (cache.TryGetValue(path, out var known)) return known;
        (Vector3 Min, Vector3 Max)? result = null;
        try
        {
            var rel = path.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase) || path.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase) ? path : "meshes\\" + path;
            if (assets.TryOpen(rel, out var stream))
            {
                byte[] bytes;
                using (stream) { using var ms = new MemoryStream(); stream.CopyTo(ms); bytes = ms.ToArray(); }
                var mesh = PenguLOD.Meshes.Nif.NifGeometryReader.Read(rel, bytes);
                var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
                bool any = false;
                foreach (var part in mesh.Parts)
                    foreach (var p in part.Positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); any = true; }
                if (any) result = (min, max);
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException or NotSupportedException or IOException) { }
        cache[path] = result;
        return result;
    }
}
