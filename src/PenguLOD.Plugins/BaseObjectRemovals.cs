using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using PenguLOD.Core.World;
using PenguLOD.Core.Pipeline;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace PenguLOD.Plugins;

public sealed record BaseObjectRemoval(string Reference, string Base, string Worldspace, string File, int Line);
public sealed record BaseObjectSwapDiagnostic(string File, int Line, string Reason);
public sealed record BaseObjectSwapReport(bool RuntimeInstalled, int ConfigFiles, int RemovalRules,
    IReadOnlyList<BaseObjectRemoval> RemovedReferences, IReadOnlyList<BaseObjectSwapDiagnostic> Diagnostics,
    int SwappedReferences = 0, int TransformedReferences = 0);

/// <summary>
/// Evaluates Base Object Swapper (<c>*_SWAP.ini</c>) rules for static LOD:
/// deterministic invisible-marker removals (<c>XMarker</c>), deterministic <c>100%</c>
/// visible model swaps (<c>STAT</c>, <c>MSTT</c>, <c>ACTI</c>, <c>TREE</c>, <c>FURN</c>, <c>CONT</c>), and deterministic
/// <c>[Transforms]</c> / inline <c>posA</c>/<c>posR</c>/<c>rotA</c>/<c>rotR</c>/<c>scale</c>/<c>scaleA</c> adjustments.
/// Non-deterministic <c>chance(&lt;100)</c> / <c>chanceS</c> / <c>chanceR</c> rules block earlier unconditional removals and leave the winning base intact.
/// </summary>
internal sealed partial class BaseObjectRemovals
{
    internal readonly record struct TransformOverride(
        Vector3? PosAbs, Vector3? PosRel, Vector3? RotAbsRad, Vector3? RotRelRad, float? Scale, bool ScaleIsAbs)
    {
        public bool HasAny => PosAbs.HasValue || PosRel.HasValue || RotAbsRad.HasValue || RotRelRad.HasValue || Scale.HasValue;

        public (Vector3 Pos, Vector3 RotRad, float Scale) Apply(Vector3 pos, Vector3 rotRad, float scale)
        {
            if (PosAbs is { } pa) pos = pa;
            if (PosRel is { } pr) pos += pr;
            if (RotAbsRad is { } ra) rotRad = ra;
            if (RotRelRad is { } rr) rotRad += rr;
            if (Scale is { } sc) scale = ScaleIsAbs ? sc : scale * sc;
            return (pos, rotRad, scale);
        }
    }

    private sealed record Rule(
        bool Remove,
        FormKey[]? Targets,
        TransformOverride Transform,
        string File,
        int Line);

    private sealed record ConditionalRule(
        HashSet<string>? Worldspaces,
        HashSet<FormKey>? Cells,
        Rule Inner);

    private static readonly FormKey XMarker = new(ModKey.FromNameAndExtension("Skyrim.esm"), 0x3B);
    private readonly Dictionary<FormKey, Rule> _references = [], _forms = [];
    private readonly Dictionary<FormKey, TransformOverride> _refTransforms = [], _formTransforms = [];
    private readonly Dictionary<FormKey, List<ConditionalRule>> _conditionalRules = [];
    private readonly HashSet<FormKey> _conditionalForms = [];
    private readonly List<BaseObjectRemoval> _removed = [];
    private readonly List<BaseObjectSwapDiagnostic> _diagnostics = [];
    private readonly HashSet<FormKey> _excluded = [];
    private readonly HashSet<FormKey> _swappedRefs = [];
    private readonly HashSet<FormKey> _transformedRefs = [];
    private readonly HashSet<FormKey> _swapTargets = [];
    private bool _runtime;
    private int _files;

    public IReadOnlySet<FormKey> Excluded => _excluded;
    public IReadOnlySet<FormKey> SwapTargets => _swapTargets;
    public BaseObjectSwapReport Report => new(_runtime, _files,
        _references.Values.Count(r => r.Remove) + _forms.Count(p => p.Value.Remove && !_conditionalForms.Contains(p.Key)),
        _removed.ToArray(), _diagnostics.ToArray(), _swappedRefs.Count, _transformedRefs.Count);

    public static BaseObjectRemovals Load(GameContext game, IAssetSource assets, CancellationToken ct, IProgress<string>? progress = null)
    {
        var result = new BaseObjectRemovals();
        var files = game.RootFiles().Where(f => f.Name.EndsWith("_SWAP.ini", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetFileName(f.FullPath), StringComparer.Ordinal).ToArray();
        result._files = files.Length;
        result._runtime = assets.Exists(@"skse\plugins\po3_BaseObjectSwapper.dll");
        if (!result._runtime || files.Length == 0) return result;
        progress?.Report($"Reading {files.Length:N0} Base Object Swapper configuration files...");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var runtimeIds = RuntimeFormIds.FromLoadOrder(game.LoadOrder);
        var resolved = new Dictionary<(string, Type), IMajorRecordGetter?>();
        IMajorRecordGetter? Resolve<T>(string identifier) where T : class, IMajorRecordGetter
        {
            identifier = identifier.Trim();
            if (resolved.TryGetValue((identifier, typeof(T)), out var cached)) return cached;
            T? record = null;
            int tilde = identifier.IndexOf('~');
            if (tilde > 0)
            {
                if (Hex(identifier[..tilde], out uint id) && id <= 0xFFFFFF
                    && SeasonalLod.PluginForm(identifier[(tilde + 1)..].Trim(), id) is { } key)
                    game.LinkCache.TryResolve<T>(key, out record);
            }
            else if (Hex(identifier, out uint id))
            {
                if (runtimeIds.ToFormKey(id) is { } key) game.LinkCache.TryResolve<T>(key, out record);
            }
            else game.LinkCache.TryResolve<T>(identifier, out record);
            resolved[(identifier, typeof(T))] = record;
            return record;
        }

        static bool IsSwappableLodBase(IPlaceableObjectGetter r) =>
            r is IStaticGetter or IMoveableStaticGetter or IActivatorGetter or ITreeGetter or IFurnitureGetter or IContainerGetter;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            string name = Path.GetFileName(file.FullPath);
            if (timer.Elapsed.TotalSeconds >= 1)
            { progress?.Report($"Base Object Swapper: reading {name}"); timer.Restart(); }
            string[] lines;
            try { lines = File.ReadAllLines(file.FullPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new IOException($"Cannot read Base Object Swapper configuration {name}; LOD exclusions cannot be determined.", ex); }
            string section = "";
            for (int i = 0; i < lines.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] is ';' or '#') continue;
                if (line[0] == '[' && line.EndsWith(']')) { section = line[1..^1]; continue; }
                if (section.Length == 0) continue;
                void Diagnostic(string reason) => result._diagnostics.Add(new(name, i + 1, reason));

                var secParts = section.Split('|');
                string secHead = secParts[0].Trim();
                bool conditional = secParts.Length > 1;
                bool isForms = secHead.Equals("Forms", StringComparison.OrdinalIgnoreCase);
                bool isRefs = secHead.Equals("References", StringComparison.OrdinalIgnoreCase);
                bool isTransforms = secHead.Equals("Transforms", StringComparison.OrdinalIgnoreCase);

                if (isTransforms && !conditional)
                {
                    var tfFields = line.Split('|');
                    if (tfFields.Length < 2) { Diagnostic("Malformed transform entry."); continue; }
                    bool tfCertain = tfFields.Length < 3 || CertainChance(tfFields[2]);
                    if (!tfCertain || !TryParseTransforms(tfFields[1], out var tfOnly) || !tfOnly.HasAny)
                    {
                        Diagnostic("Random or unsupported transform override is not applied to LOD.");
                        continue;
                    }
                    foreach (var rawId in tfFields[0].Split(','))
                    {
                        if (Resolve<IPlacedObjectGetter>(rawId) is { } pr) result._refTransforms[pr.FormKey] = tfOnly;
                        else if (Resolve<IPlaceableObjectGetter>(rawId) is { } pb) result._formTransforms[pb.FormKey] = tfOnly;
                    }
                    continue;
                }

                if (secHead.Equals("Properties", StringComparison.OrdinalIgnoreCase) || isTransforms || (conditional && !isForms))
                { Diagnostic("Property overrides are not applied to LOD."); continue; }
                if (!isForms && !isRefs)
                { Diagnostic("Unsupported swap section."); continue; }

                var fields = line.Split('|');
                if (fields.Length < 2) { Diagnostic("Malformed swap entry."); continue; }
                var sources = fields[0].Split(',').Select(id => isForms ? Resolve<IPlaceableObjectGetter>(id) : Resolve<IPlacedObjectGetter>(id))
                    .OfType<IMajorRecordGetter>().Select(r => r.FormKey).Distinct().ToArray();
                if (sources.Length == 0) { Diagnostic("Source is unresolved in the selected load order."); continue; }

                var targetRecords = fields[1].Split(',').Select(Resolve<IPlaceableObjectGetter>).OfType<IPlaceableObjectGetter>()
                    .Where(r => !r.IsDeleted).DistinctBy(r => r.FormKey).ToArray();
                var targets = targetRecords.Select(r => r.FormKey).ToArray();
                if (targets.Length == 0) { Diagnostic("Replacement is unresolved or is not a placeable object."); continue; }
                result._swapTargets.UnionWith(targets);

                bool thirdIsChance = fields.Length == 3 && Chance().IsMatch(fields[2]);
                string transformField = fields.Length >= 3 && !thirdIsChance ? fields[2] : "";
                string chanceField = fields.Length >= 4 ? fields[3] : (thirdIsChance ? fields[2] : "");

                TransformOverride inlineTf = default;
                bool tfDeterministic = string.IsNullOrWhiteSpace(transformField) || TryParseTransforms(transformField, out inlineTf);
                bool certain = string.IsNullOrWhiteSpace(chanceField) || CertainChance(chanceField);

                if (conditional)
                {
                    result._conditionalForms.UnionWith(sources);
                    if (certain && targets.Length == 1 && targets[0] != XMarker && targetRecords.All(IsSwappableLodBase)
                        && TryResolveConditions(secParts[1], Resolve<IWorldspaceGetter>, Resolve<ICellGetter>, out var wsFilter, out var cellFilter))
                    {
                        var condRule = new ConditionalRule(wsFilter, cellFilter,
                            new Rule(false, targets, tfDeterministic ? inlineTf : default, name, i + 1));
                        foreach (var src in sources)
                        {
                            if (!result._conditionalRules.TryGetValue(src, out var list)) result._conditionalRules[src] = list = [];
                            list.Add(condRule);
                        }
                        continue;
                    }
                    Diagnostic("Conditional base swaps are not applied; they block unconditional base removals.");
                    continue;
                }

                bool remove = targets.Length == 1 && targets[0] == XMarker && certain;
                FormKey[]? activeTargets = !remove && certain && targets.Length == 1 && targetRecords.All(IsSwappableLodBase)
                    ? targets : null;

                foreach (var source in sources)
                {
                    if (targets.Length == 1 && source == targets[0]
                        && (transformField.Length == 0 || !PropertyFunction().IsMatch(transformField))) continue;
                    (isForms ? result._forms : result._references)[source] =
                        new Rule(remove, activeTargets, tfDeterministic ? inlineTf : default, name, i + 1);
                }

                if (!remove && activeTargets is null)
                {
                    Diagnostic(!certain ? "Chance-based swap is not evaluated."
                        : targets.Length > 1 ? "Random replacement choice is not evaluated."
                        : "Replacement object type is not supported for static LOD.");
                }
            }
        }
        return result;
    }

    public bool Exclude(IPlacedObjectGetter reference, string worldspace)
    {
        if (reference.Base.FormKeyNullable is not { } baseKey) return false;
        if (!_references.TryGetValue(reference.FormKey, out var rule))
        {
            if (_conditionalForms.Contains(baseKey) || !_forms.TryGetValue(baseKey, out rule)) return false;
        }
        if (!rule.Remove) return false;
        if (_excluded.Add(reference.FormKey))
            _removed.Add(new(reference.FormKey.ToString(), baseKey.ToString(), worldspace, rule.File, rule.Line));
        return true;
    }

    /// <summary>
    /// Resolves deterministic or <c>chanceS</c>-seeded base object swap and placement transform for an exterior placed object.
    /// </summary>
    public (FormKey EffectiveBase, Vector3 Position, Vector3 RotationRadians, float Scale) ResolvePlacement(
        IPlacedObjectGetter reference, FormKey originalBase, string worldspace, FormKey? cellKey,
        Vector3 position, Vector3 rotationRadians, float scale)
    {
        FormKey effectiveBase = originalBase;
        TransformOverride ruleTf = default;

        Rule? chosen = null;
        if (_references.TryGetValue(reference.FormKey, out var refRule))
        {
            chosen = refRule;
        }
        else if (_conditionalRules.TryGetValue(originalBase, out var condList))
        {
            for (int i = condList.Count - 1; i >= 0; i--)
            {
                var c = condList[i];
                bool wsOk = c.Worldspaces is null || c.Worldspaces.Contains(worldspace);
                bool cellOk = c.Cells is null || (cellKey is { } ck && c.Cells.Contains(ck));
                if (wsOk && cellOk) { chosen = c.Inner; break; }
            }
        }
        else if (!_conditionalForms.Contains(originalBase) && _forms.TryGetValue(originalBase, out var formRule))
        {
            chosen = formRule;
        }

        if (chosen is { Remove: false, Targets: { Length: 1 } targets })
        {
            ruleTf = chosen.Transform;
            effectiveBase = targets[0];
        }

        if (effectiveBase != originalBase) _swappedRefs.Add(reference.FormKey);

        bool anyTransform = false;
        if (_formTransforms.TryGetValue(originalBase, out var ftf) && ftf.HasAny)
        {
            (position, rotationRadians, scale) = ftf.Apply(position, rotationRadians, scale);
            anyTransform = true;
        }
        if (_refTransforms.TryGetValue(reference.FormKey, out var rtf) && rtf.HasAny)
        {
            (position, rotationRadians, scale) = rtf.Apply(position, rotationRadians, scale);
            anyTransform = true;
        }
        if (ruleTf.HasAny && (effectiveBase != originalBase || chosen?.Targets is { Length: 1 } self && self[0] == originalBase))
        {
            (position, rotationRadians, scale) = ruleTf.Apply(position, rotationRadians, scale);
            anyTransform = true;
        }
        if (anyTransform) _transformedRefs.Add(reference.FormKey);

        return (effectiveBase, position, rotationRadians, scale);
    }

    private static bool TryResolveConditions(
        string conditionList,
        Func<string, IMajorRecordGetter?> resolveWorld,
        Func<string, IMajorRecordGetter?> resolveCell,
        out HashSet<string>? worldspaces,
        out HashSet<FormKey>? cells)
    {
        worldspaces = null;
        cells = null;
        foreach (var raw in conditionList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.StartsWith('-') || raw.Contains('(')) return false; // exclusions / function conditions stay conservative
            if (resolveWorld(raw) is IWorldspaceGetter ws && !string.IsNullOrEmpty(ws.EditorID))
            {
                worldspaces ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                worldspaces.Add(ws.EditorID);
            }
            else if (resolveCell(raw) is ICellGetter cell)
            {
                cells ??= [];
                cells.Add(cell.FormKey);
            }
            else return false;
        }
        return worldspaces is not null || cells is not null;
    }

    private static bool TryParseTransforms(string traits, out TransformOverride tf)
    {
        tf = default;
        if (traits.Contains('/')) return false; // BOS min/max random range e.g. scale(0.8/1.2) is non-deterministic
        const float DegToRad = MathF.PI / 180f;
        Vector3? posA = null, posR = null, rotA = null, rotR = null;
        float? scale = null;
        bool scaleAbs = false;

        foreach (Match m in Vec3TraitRegex().Matches(traits))
        {
            if (!float.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                || !float.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
                || !float.TryParse(m.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                return false;
            switch (m.Groups[1].Value)
            {
                case "posA": posA = new Vector3(x, y, z); break;
                case "posR": posR = new Vector3(x, y, z); break;
                case "rotA": rotA = new Vector3(x * DegToRad, y * DegToRad, z * DegToRad); break;
                case "rotR": rotR = new Vector3(x * DegToRad, y * DegToRad, z * DegToRad); break;
            }
        }
        var sm = ScaleTraitRegex().Match(traits);
        if (sm.Success)
        {
            if (!float.TryParse(sm.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float s) || s <= 0f || !float.IsFinite(s))
                return false;
            scaleAbs = sm.Groups[1].Value.EndsWith('A');
            // In BOS (ScaleRange::SetScale), scaleA() writes directly to TESObjectREFR::refScale in percent (100 = 1.0),
            // whereas scale() multiplies the existing scale factor.
            scale = scaleAbs ? s / 100f : s;
        }
        tf = new TransformOverride(posA, posR, rotA, rotR, scale, scaleAbs);
        return true;
    }

    private static bool CertainChance(string traits)
    {
        if (!traits.Contains("chance", StringComparison.Ordinal)) return true;
        var match = Chance().Match(traits);
        return match.Success && float.TryParse(match.Groups[1].Value, NumberStyles.Float,
            CultureInfo.InvariantCulture, out float value) && float.IsFinite(value) && value >= 100;
    }
    private static bool Hex(string text, out uint id) => uint.TryParse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? text[2..] : text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out id);

    [GeneratedRegex(@"chance[SLR]?\(\s*([^,)]+)")]
    private static partial Regex Chance();
    [GeneratedRegex(@"\b(posA|posR|rotA|rotR)\(\s*([^,)]+)\s*,\s*([^,)]+)\s*,\s*([^,)]+)\s*\)")]
    private static partial Regex Vec3TraitRegex();
    [GeneratedRegex(@"\b(scaleA?)\(\s*([^,)]+)\s*\)")]
    private static partial Regex ScaleTraitRegex();
    [GeneratedRegex(@"(?:pos[AR]?|rot[AR]?|scaleA?|flagsC?)\(")]
    private static partial Regex PropertyFunction();
}
