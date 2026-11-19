using PenguLOD.Core.Paths;
using PenguLOD.Core.World;

namespace PenguLOD.Plugins.Mo2;

/// <summary>
/// One MO2 profile: which mods are enabled and in what priority, the plugin load order,
/// and the INI archive list the game would use.
/// </summary>
public sealed class Mo2Profile
{
    public Mo2Instance Instance { get; }
    public string Name { get; }
    public string Folder { get; }

    /// <summary>Enabled mod folders, LOWEST priority first (the order files should be layered in).</summary>
    public IReadOnlyList<string> EnabledModsLowToHigh { get; }

    /// <summary>Enabled plugin file names in load order (base masters and CC included).</summary>
    public IReadOnlyList<string> EnabledPlugins { get; }

    public bool UsesLocalInis { get; }

    /// <summary>The folder the profile's mods are in: the instance's, or for an Amethyst profile with its own mods, the profile's.</summary>
    public string ModsFolder { get; }
    public string OverwriteFolder { get; }

    /// <summary>For an Amethyst profile: how it changes where each mod's files land. Null for MO2.</summary>
    public AmethystProfileRules? AmethystRules { get; }

    /// <summary>Enabled Amethyst mods left out because their separator deploys them somewhere other than Data.</summary>
    public IReadOnlyList<string> RoutedElsewhere { get; } = [];

    /// <summary>The folder the profile's local INIs are in: MO2 keeps them in the profile, Amethyst in its "ini files" subfolder.</summary>
    public string LocalIniFolder => _iniFolder;
    private readonly string _iniFolder;

    /// <summary>The folders of the enabled mods that exist on disk, plus Overwrite (lowest priority first, Overwrite last).</summary>
    public IReadOnlyList<string> EnabledModFolders()
    {
        var folders = EnabledModsLowToHigh.Select(m => GamePath.ResolveExisting(ModsFolder, m)).OfType<string>().Where(Directory.Exists).ToList();
        if (Directory.Exists(OverwriteFolder)) folders.Add(OverwriteFolder);
        return folders;
    }

    /// <summary>The Amethyst file rules of one layer (a mod folder or Overwrite), or null when its files land as they are.</summary>
    public AmethystLayerRules? LayerRules(string layer)
    {
        if (AmethystRules is null) return null;
        string name = HostPath.Canonical(layer) == HostPath.Canonical(OverwriteFolder) ? AmethystStaging.OverwriteName : Path.GetFileName(Path.TrimEndingDirectorySeparator(layer));
        return AmethystRules.For(name, layer);
    }

    /// <summary>Plugins ticked in plugins.txt. Base masters and Skyrim.ccc entries are implicit and may legitimately be absent.</summary>
    public IReadOnlySet<string> ExplicitlyEnabled { get; }

    private static readonly string[] BaseMasters =
        ["Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm"];

    private Mo2Profile(Mo2Instance instance, string name, string folder)
    {
        Instance = instance;
        Name = name;
        Folder = folder;
        ModsFolder = instance.ModsFolder;
        OverwriteFolder = instance.OverwriteFolder;
        _iniFolder = folder;
        if (instance.Amethyst is { } amethyst)
        {
            var rules = AmethystRules = AmethystProfileRules.Read(folder);
            if (rules.ProfileSpecificMods) { ModsFolder = Path.Combine(folder, "mods"); OverwriteFolder = Path.Combine(folder, "overwrite"); }
            var routed = new List<string>();
            EnabledModsLowToHigh = ReadModList(Path.Combine(folder, "modlist.txt"), amethyst: true, rules.RoutedSeparators, routed);
            RoutedElsewhere = routed;
            _iniFolder = Path.Combine(folder, "ini files");
            UsesLocalInis = amethyst.ProfileIniFiles && Directory.Exists(_iniFolder);
        }
        else
        {
            EnabledModsLowToHigh = ReadModList(Path.Combine(folder, "modlist.txt"));
            UsesLocalInis = ReadLocalSettingsFlag(Path.Combine(folder, "settings.ini"));
        }
        EnabledPlugins = ReadPlugins(folder, instance.GamePath, out var explicitSet);
        ExplicitlyEnabled = explicitSet;
    }

    public static Mo2Profile Open(Mo2Instance instance, string name)
    {
        var folder = Path.Combine(instance.ProfilesFolder, name);
        if (!File.Exists(Path.Combine(folder, "modlist.txt")))
            throw new DirectoryNotFoundException($"{(instance.Amethyst is null ? "MO2" : "Amethyst")} profile \"{name}\" not found in {instance.ProfilesFolder}.");
        return new Mo2Profile(instance, name, folder);
    }

    /// <summary>
    /// modlist.txt is written highest priority first. "+" = enabled mod, "-" = disabled,
    /// "*" = unmanaged (DLC/CC already in the game Data folder). Separators end in "_separator".
    /// <para>Amethyst writes the same format, with two differences: "*" is an enabled mod that cannot be switched off, and a
    /// separator can deploy the mods listed after it (until the next separator) somewhere other than Data. Those are left out and
    /// listed in <paramref name="routedElsewhere"/>.</para>
    /// </summary>
    internal static List<string> ReadModList(string path, bool amethyst = false, IReadOnlySet<string>? routedSeparators = null, List<string>? routedElsewhere = null)
    {
        var enabledHighToLow = new List<string>();
        bool routed = false;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.TrimEnd();
            if (line.Length < 2) continue;
            var name = line[1..];
            if (name.EndsWith("_separator", StringComparison.OrdinalIgnoreCase))
            {
                routed = amethyst && routedSeparators?.Contains(name) == true;
                continue;
            }
            if (line[0] != '+' && !(amethyst && line[0] == '*')) continue;
            if (routed) { routedElsewhere?.Add(name); continue; }
            enabledHighToLow.Add(name);
        }
        enabledHighToLow.Reverse();
        return enabledHighToLow;
    }

    private static bool ReadLocalSettingsFlag(string settingsIni)
    {
        if (!File.Exists(settingsIni)) return false;
        var ini = Mo2Instance.ReadIni(settingsIni);
        return ini.TryGetValue("General", out var g)
               && g.TryGetValue("LocalSettings", out var v)
               && v.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Order comes from loadorder.txt (it includes base masters and CC); "enabled" comes from
    /// plugins.txt ("*" prefix) plus the implicitly-active base masters and Skyrim.ccc entries.
    /// </summary>
    internal static List<string> ReadPlugins(string profileFolder, string gamePath, out HashSet<string> explicitlyEnabled)
    {
        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pluginsTxtOrder = new List<string>();

        var pluginsTxt = Path.Combine(profileFolder, "plugins.txt");
        if (File.Exists(pluginsTxt))
        {
            foreach (var raw in File.ReadLines(pluginsTxt))
            {
                var line = raw.Trim();
                if (line.Length < 2 || line[0] == '#' || line[0] != '*') continue;
                var name = line[1..];
                if (enabled.Add(name)) pluginsTxtOrder.Add(name);
            }
        }

        explicitlyEnabled = new HashSet<string>(enabled, StringComparer.OrdinalIgnoreCase);
        foreach (var m in BaseMasters) enabled.Add(m);
        var ccc = Path.Combine(gamePath, "Skyrim.ccc");
        if (File.Exists(ccc))
            foreach (var l in File.ReadLines(ccc).Select(l => l.Trim()).Where(l => l.Length > 0))
                enabled.Add(l);

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var loadOrderTxt = Path.Combine(profileFolder, "loadorder.txt");
        if (File.Exists(loadOrderTxt))
        {
            foreach (var raw in File.ReadLines(loadOrderTxt))
            {
                var name = raw.Trim();
                if (name.Length == 0 || name[0] == '#') continue;
                if (enabled.Contains(name) && seen.Add(name)) result.Add(name);
            }
        }
        else
        {
            foreach (var m in BaseMasters) if (seen.Add(m)) result.Add(m);
        }

        // Anything enabled in plugins.txt but missing from loadorder.txt goes at the end, in plugins.txt order.
        foreach (var p in pluginsTxtOrder)
            if (seen.Add(p)) result.Add(p);

        return result;
    }

    /// <summary>
    /// sResourceArchiveList + sResourceArchiveList2 from the profile's skyrim.ini (when the profile
    /// uses local INIs) or the user's Documents INI; falls back to the SSE defaults.
    /// </summary>
    public IReadOnlyList<string> IniArchives(string? overrideIni = null, bool vr = false)
        => SkyrimArchives.ReadIniLists(IniPaths(overrideIni, vr));

    /// <summary>Game INIs in priority order, highest first, honoring profile-local settings.</summary>
    internal IReadOnlyList<string> IniPaths(string? overrideIni = null, bool vr = false)
    {
        var candidates = new List<string>();
        if (UsesLocalInis)
        {
            candidates.Add(GamePath.ResolveExisting(_iniFolder, "skyrimcustom.ini") ?? Path.Combine(_iniFolder, "skyrimcustom.ini"));
            candidates.Add(GamePath.ResolveExisting(_iniFolder, "skyrim.ini") ?? Path.Combine(_iniFolder, "skyrim.ini"));
        }
        foreach (var docs in HostPath.GameDocuments(Instance.WinePrefix, vr))
        {
            candidates.Add(Path.Combine(docs, "SkyrimCustom.ini"));
            candidates.Add(Path.Combine(docs, "Skyrim.ini"));
        }
        candidates.Add(Path.Combine(Instance.GamePath, "Skyrim_Default.ini"));
        if (overrideIni is not null) candidates.Insert(0, overrideIni);
        return candidates;
    }
}
