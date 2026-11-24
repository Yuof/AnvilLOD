using PenguLOD.Core.Pipeline;
using PenguLOD.Core.Paths;
using System.Diagnostics;
using PenguLOD.Core.World;

namespace PenguLOD.Plugins.Mo2;

/// <summary>
/// Our own read-only stand-in for MO2's virtual Data folder: game Data, then every enabled mod in
/// priority order, then overwrite. Later layers win, exactly as in MO2.
/// <para>
/// Only what LOD needs is indexed: root-level files (plugins, BSAs) and the subtrees named in
/// <c>looseRoots</c> (e.g. meshes, lodsettings). Each layer is enumerated in parallel.
/// </para>
/// </summary>
public sealed class VirtualDataIndex
{
    /// <summary>Layers lowest priority first.</summary>
    public IReadOnlyList<string> Layers { get; }

    private readonly Dictionary<string, string> _root;   // "skyrim.esm" -> full path
    private readonly Dictionary<string, string> _loose;  // "meshes\\foo\\bar.nif" -> full path

    public int LooseFileCount => _loose.Count;
    public TimeSpan BuildTime { get; }
    public int MissingModFolders { get; }
    public string? ExcludedLayer { get; private init; }
    /// <summary>Mods with folders that differ only in case ("meshes" and "Meshes"), as "mod: folder, folder". Their files are merged.</summary>
    public IReadOnlyList<string> CaseConflicts { get; private init; } = [];
    private IReadOnlyList<string> _indexedRoots = [];
    // Amethyst file rules per layer (null where files land as they are), parallel to Layers.
    private IReadOnlyList<AmethystLayerRules?> _rules = [];

    private VirtualDataIndex(IReadOnlyList<string> layers, Dictionary<string, string> root,
        Dictionary<string, string> loose, TimeSpan time, int missing)
    {
        Layers = layers;
        _root = root;
        _loose = loose;
        BuildTime = time;
        MissingModFolders = missing;
    }

    public static VirtualDataIndex Build(Mo2Profile profile, IReadOnlyCollection<string> looseRoots, string? excludeFolder = null,
        IReadOnlyList<string>? excludeFolders = null)
    {
        string? exclude = string.IsNullOrWhiteSpace(excludeFolder) ? null : Path.GetFullPath(excludeFolder).TrimEnd('\\', '/');
        string? excluded = null;
        var sw = Stopwatch.StartNew();
        var inst = profile.Instance;

        var layerEntries = new List<(string Dir, AmethystLayerRules? Rules)> { (inst.GameDataFolder, null) };
        int missing = 0;
        void AddModLayer(string dir, AmethystLayerRules? layerRules)
        {
            layerEntries.Add((dir, layerRules));
            foreach (var rootData in GamePath.ResolveAllExisting(dir, @"Root\Data").Where(Directory.Exists))
                layerEntries.Add((rootData, null));
        }

        foreach (var mod in profile.EnabledModsLowToHigh)
        {
            var dir = GamePath.ResolveExisting(profile.ModsFolder, mod) ?? Path.Combine(profile.ModsFolder, mod);
            if ((exclude is not null && HostPath.Canonical(dir) == HostPath.Canonical(exclude))
                || (excludeFolders?.Any(p => HostPath.Canonical(dir) == HostPath.Canonical(p)) ?? false))
            {
                excluded = dir;
                continue;
            }
            if (Directory.Exists(dir)) AddModLayer(dir, profile.LayerRules(dir));
            else missing++;
        }
        if (Directory.Exists(profile.OverwriteFolder)) AddModLayer(profile.OverwriteFolder, profile.LayerRules(profile.OverwriteFolder));
        var layers = layerEntries.Select(e => e.Dir).ToList();
        var rules = layerEntries.Select(e => e.Rules).ToArray();

        // Enumerate every layer in parallel, then merge in priority order.
        var perLayer = new (List<KeyValuePair<string, string>> Root, List<KeyValuePair<string, string>> Loose, List<string> Conflicts)[layers.Count];
        Parallel.For(0, layers.Count, RunLimits.Parallel(), i => perLayer[i] = rules[i] is { } r
            ? ScanRuledLayer(layers[i], r, looseRoots, inst.SkipDirectories)
            : ScanLayer(layers[i], looseRoots, inst.SkipDirectories));

        var root = new Dictionary<string, string>(StringComparer.Ordinal);
        var loose = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (r, l, _) in perLayer)
        {
            foreach (var kv in r) root[kv.Key] = kv.Value;
            foreach (var kv in l) loose[kv.Key] = kv.Value;
        }

        return new VirtualDataIndex(layers, root, loose, sw.Elapsed, missing)
        {
            ExcludedLayer = excluded,
            CaseConflicts = perLayer.SelectMany(p => p.Conflicts).ToArray(),
            _indexedRoots = looseRoots.Select(r => r.Replace('/', '\\').Trim('\\').ToLowerInvariant() + "\\").ToList(),
            _rules = rules,
        };
    }

    /// <summary>
    /// A layer whose Amethyst rules move or leave out files: every file is walked, mapped to where it lands in Data, and kept when
    /// that is the Data root or under an indexed root.
    /// </summary>
    private static (List<KeyValuePair<string, string>>, List<KeyValuePair<string, string>>, List<string>) ScanRuledLayer(
        string layer, AmethystLayerRules rules, IReadOnlyCollection<string> looseRoots, IReadOnlySet<string> skipDirs)
    {
        var root = new List<KeyValuePair<string, string>>();
        var loose = new List<KeyValuePair<string, string>>();
        var prefixes = looseRoots.Select(r => r.Replace('/', '\\').Trim('\\').ToLowerInvariant() + "\\").ToArray();
        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false };
        var stack = new Stack<(string Dir, string Raw)>();
        stack.Push((layer, ""));
        try
        {
            while (stack.Count > 0)
            {
                var (d, raw) = stack.Pop();
                foreach (var f in Directory.EnumerateFiles(d, "*", options))
                {
                    var file = Path.GetFileName(f);
                    if (file.EndsWith(".mohidden", StringComparison.OrdinalIgnoreCase)) continue;
                    if (rules.Map(raw + file.ToLowerInvariant()) is not { } mapped) continue;
                    var key = mapped.Replace('/', '\\');
                    if (!key.Contains('\\')) root.Add(new(key, f));
                    else if (prefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal))) loose.Add(new(key, f));
                }
                foreach (var s in Directory.EnumerateDirectories(d, "*", options))
                {
                    var n = Path.GetFileName(s);
                    if (skipDirs.Contains(n) || n.EndsWith(".mohidden", StringComparison.OrdinalIgnoreCase)) continue;
                    stack.Push((s, raw + n.ToLowerInvariant() + "/"));
                }
            }
        }
        catch (DirectoryNotFoundException) { }
        catch (UnauthorizedAccessException) { }
        return (root, loose, []);
    }

    private static (List<KeyValuePair<string, string>>, List<KeyValuePair<string, string>>, List<string>) ScanLayer(
        string layer, IReadOnlyCollection<string> looseRoots, IReadOnlySet<string> skipDirs)
    {
        var conflicts = new List<string>();
        var root = new List<KeyValuePair<string, string>>();
        var loose = new List<KeyValuePair<string, string>>();
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.System,
        };

        try
        {
            foreach (var f in Directory.EnumerateFiles(layer, "*", options))
            {
                if (f.EndsWith(".mohidden", StringComparison.OrdinalIgnoreCase)) continue;
                root.Add(new(Path.GetFileName(f).ToLowerInvariant(), f));
            }

            // Roots may be nested ("textures\\terrain\\lodgen"), so only that subtree is walked.
            foreach (var root0 in looseRoots)
            {
                var rel = root0.Replace('/', '\\').Trim('\\').ToLowerInvariant();
                // Folders that differ only in case are merged, as the game sees one folder. Within the layer, files of
                // a later folder in ordinal order replace those of an earlier one.
                var dirs = GamePath.ResolveAllExisting(layer, rel).Where(Directory.Exists).ToArray();
                if (dirs.Length > 1)
                    conflicts.Add($"{Path.GetFileName(Path.TrimEndingDirectorySeparator(layer))}: {string.Join(", ", dirs.Select(d => Path.GetRelativePath(layer, d)))}");
                foreach (var dir in dirs) WalkLoose(dir, rel, loose, skipDirs);
            }
        }
        catch (DirectoryNotFoundException) { }
        catch (UnauthorizedAccessException) { }

        return (root, loose, conflicts);
    }

    private static void WalkLoose(string dir, string relPrefix, List<KeyValuePair<string, string>> into, IReadOnlySet<string> skipDirs)
    {
        var stack = new Stack<(string Dir, string Rel)>();
        stack.Push((dir, relPrefix));
        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false };

        while (stack.Count > 0)
        {
            var (d, rel) = stack.Pop();
            foreach (var f in Directory.EnumerateFiles(d, "*", options))
            {
                var file = Path.GetFileName(f);
                if (file.EndsWith(".mohidden", StringComparison.OrdinalIgnoreCase)) continue;
                into.Add(new(rel + "\\" + file.ToLowerInvariant(), f));
            }
            foreach (var s in Directory.EnumerateDirectories(d, "*", options))
            {
                var n = Path.GetFileName(s);
                if (skipDirs.Contains(n) || n.EndsWith(".mohidden", StringComparison.OrdinalIgnoreCase)) continue;
                stack.Push((s, rel + "\\" + n.ToLowerInvariant()));
            }
        }
    }

    /// <summary>Full path of a root-level file (plugin or BSA) as MO2 would resolve it.</summary>
    public string? ResolveRootFile(string fileName) =>
        _root.TryGetValue(fileName.ToLowerInvariant(), out var p) ? p : null;

    public IEnumerable<string> RootFileNames => _root.Keys;

    /// <summary>Normalized data-relative paths of indexed loose files under a prefix ("meshes\\").</summary>
    public IEnumerable<string> EnumerateLoose(string prefix)
    {
        var p = GamePath.Normalize(prefix);
        foreach (var k in _loose.Keys)
            if (k.StartsWith(p, StringComparison.Ordinal)) yield return k;
    }

    /// <summary>Full path of a loose file under an indexed root, or null.</summary>
    public string? ResolveLoose(string dataRelativePath) =>
        _loose.TryGetValue(GamePath.Normalize(dataRelativePath), out var p) ? p : null;

    /// <summary>
    /// A loose file outside the indexed roots (a texture, say), looked up on disk layer by layer, highest priority
    /// first. Inside an indexed root the index is the answer.
    /// </summary>
    public string? FindLoose(string dataRelativePath)
    {
        var key = GamePath.Normalize(dataRelativePath);
        if (_loose.TryGetValue(key, out var p)) return p;
        foreach (var r in _indexedRoots)
            if (key.StartsWith(r, StringComparison.Ordinal)) return null;
        for (int i = Layers.Count - 1; i >= 0; i--)
        {
            if (i < _rules.Count && _rules[i] is { } rules)
            {
                // The file can come from any path the layer's rules map to it.
                var mapped = key.Replace('\\', '/');
                foreach (var source in rules.Sources(mapped))
                {
                    if (rules.Map(source) != mapped) continue;
                    var found = GamePath.ResolveAllExisting(Layers[i], source.Replace('/', '\\')).LastOrDefault(File.Exists);
                    if (found is not null && !File.Exists(found + ".mohidden")) return found;
                }
                continue;
            }
            // Same rule as the index: of case variants within one layer, the last in ordinal order wins.
            var full = GamePath.ResolveAllExisting(Layers[i], key).LastOrDefault(File.Exists);
            if (full is not null && !File.Exists(full + ".mohidden")) return full;
        }
        return null;
    }
}
