using System.Text.Json;
using PenguLOD.Core.Paths;

namespace PenguLOD.Plugins.Mo2;

/// <summary>
/// An Amethyst Mod Manager staging folder (<c>~/Games/Amethyst/&lt;game&gt;</c>): <c>mods/</c>, <c>overwrite/</c>,
/// <c>Root_Folder/</c> and <c>profiles/&lt;name&gt;/</c> with MO2-format <c>modlist.txt</c>, <c>plugins.txt</c> and
/// <c>loadorder.txt</c>. It has no <c>ModOrganizer.ini</c>; the game folder and Wine prefix are in Amethyst's settings,
/// <c>&lt;config&gt;/AmethystModManager/games/&lt;game&gt;/paths.json</c>.
/// </summary>
public sealed record AmethystStaging(string Folder, string? GameName, string? GamePath, string? WinePrefix, string? LastProfile, bool ProfileIniFiles)
{
    /// <summary>Amethyst settings files (<c>paths.json</c>) that exist but are not valid JSON, so their game could not be matched.</summary>
    public IReadOnlyList<string> UnreadableSettings { get; init; } = [];

    /// <summary>Amethyst's name for its Overwrite folder in per-mod settings.</summary>
    public const string OverwriteName = "[Overwrite]";

    /// <summary>Amethyst keeps the game's own Data folder here while a deploy has replaced Data with links.</summary>
    public const string CoreDataFolder = "Data_Core";

    /// <summary>A folder with Amethyst's layout and no ModOrganizer.ini.</summary>
    public static bool IsStaging(string folder) =>
        Directory.Exists(folder)
        && !File.Exists(Path.Combine(folder, "ModOrganizer.ini"))
        && Directory.Exists(Path.Combine(folder, "profiles"))
        && (File.Exists(Path.Combine(folder, "filegraph.sqlite3")) || Directory.Exists(Path.Combine(folder, "Root_Folder")));

    /// <summary>Where Amethyst keeps its settings: $XDG_CONFIG_HOME, ~/.config, and the Flatpak's own config folder.</summary>
    public static IEnumerable<string> ConfigFolders()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg) yield return Path.Combine(xdg, "AmethystModManager");
        yield return Path.Combine(home, ".config", "AmethystModManager");
        yield return Path.Combine(home, ".var", "app", "io.github.Amethyst.ModManager", "config", "AmethystModManager");
    }

    /// <summary>
    /// Reads the settings of the game whose staging folder this is. Settings that are not found leave their values null, so typed
    /// locations can fill them in.
    /// </summary>
    public static AmethystStaging Read(string folder, IEnumerable<string>? configFolders = null)
    {
        string staging = HostPath.Canonical(folder);
        var unreadable = new List<string>();
        foreach (string config in (configFolders ?? ConfigFolders()).Where(Directory.Exists).Distinct())
        {
            string games = Path.Combine(config, "games");
            if (!Directory.Exists(games)) continue;
            foreach (string gameFolder in Directory.EnumerateDirectories(games))
            {
                string pathsFile = Path.Combine(gameFolder, "paths.json");
                if (!File.Exists(pathsFile)) continue;
                JsonElement paths;
                try { using var doc = JsonDocument.Parse(File.ReadAllText(pathsFile)); paths = doc.RootElement.Clone(); }
                catch (JsonException) { unreadable.Add(pathsFile); continue; }
                string game = Path.GetFileName(gameFolder);
                string? stagingPath = Text(paths, "staging_path");
                // An empty staging path is the older default: <config>/Profiles/<game>, or ~/Games/Amethyst/Profiles/<game>.
                var candidates = stagingPath is not null ? [stagingPath]
                    : new[] { Path.Combine(config, "Profiles", game),
                              Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games", "Amethyst", "Profiles", game) };
                if (!candidates.Any(c => HostPath.Canonical(c) == staging)) continue;
                bool cleared = paths.TryGetProperty("prefix_path_cleared", out var c) && c.ValueKind == JsonValueKind.True;
                return new(folder, game, Text(paths, "game_path"), cleared ? null : Text(paths, "prefix_path"),
                    OpenProfile(config, game), paths.TryGetProperty("profile_ini_files", out var ini) && ini.ValueKind == JsonValueKind.True)
                    { UnreadableSettings = unreadable };
            }
        }
        return new(folder, null, null, null, null, false) { UnreadableSettings = unreadable };
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    // amethyst.ini [session] last_game / last_profile: the profile Amethyst has open.
    private static string? OpenProfile(string config, string game)
    {
        string ini = Path.Combine(config, "amethyst.ini");
        if (!File.Exists(ini)) return null;
        var session = Mo2Instance.ReadIni(ini).GetValueOrDefault("session");
        return session is not null && session.TryGetValue("last_game", out var last) && last.Equals(game, StringComparison.OrdinalIgnoreCase)
               && session.TryGetValue("last_profile", out var profile) && profile.Length > 0 ? profile : null;
    }
}

/// <summary>
/// What an Amethyst profile changes about where a mod's files land, from <c>profile_state.json</c> (or the older per-key files):
/// folders stripped from a mod's paths ("Top Level"), files left out ("Disable") or sent to the game folder ("Root"), and mods under
/// a separator that deploys somewhere other than Data. All paths are the mod's own, lower case, with forward slashes.
/// </summary>
public sealed class AmethystProfileRules
{
    public bool ProfileSpecificMods { get; private init; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> StripPrefixes { get; private init; } = new Dictionary<string, IReadOnlyList<string>>();
    public IReadOnlyDictionary<string, IReadOnlySet<string>> Excluded { get; private init; } = new Dictionary<string, IReadOnlySet<string>>();
    public IReadOnlyDictionary<string, IReadOnlySet<string>> RootFiles { get; private init; } = new Dictionary<string, IReadOnlySet<string>>();
    /// <summary>Separator names whose mods deploy to their own folder instead of Data.</summary>
    public IReadOnlySet<string> RoutedSeparators { get; private init; } = new HashSet<string>();

    public static AmethystProfileRules Read(string profileFolder)
    {
        JsonElement state = default;
        string stateFile = Path.Combine(profileFolder, "profile_state.json");
        if (File.Exists(stateFile))
            try { using var doc = JsonDocument.Parse(File.ReadAllText(stateFile)); state = doc.RootElement.Clone(); }
            catch (JsonException) { }

        // A key missing from profile_state.json may still be in its older file of its own.
        JsonElement Key(string key, string? legacyFile)
        {
            if (state.ValueKind == JsonValueKind.Object && state.TryGetProperty(key, out var v)) return v;
            if (legacyFile is not null && File.Exists(Path.Combine(profileFolder, legacyFile)))
                try { using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(profileFolder, legacyFile))); return doc.RootElement.Clone(); }
                catch (JsonException) { }
            return default;
        }
        static Dictionary<string, List<string>> Lists(JsonElement e)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (e.ValueKind != JsonValueKind.Object) return result;
            foreach (var p in e.EnumerateObject())
            {
                var items = p.Value.ValueKind switch
                {
                    JsonValueKind.String => [p.Value.GetString()!],
                    JsonValueKind.Array => p.Value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList(),
                    _ => new List<string>(),
                };
                items = items.Where(s => s.Length > 0).ToList();
                if (items.Count > 0) result[p.Name] = items;
            }
            return result;
        }
        static string Key0(string path) => path.Replace('\\', '/').Trim('/').ToLowerInvariant();

        var settings = Key("profile_settings", "profile_settings.json");
        var separators = Key("separator_deploy_paths", "separator_deploy_paths.json");
        var routed = new HashSet<string>(StringComparer.Ordinal);
        if (separators.ValueKind == JsonValueKind.Object)
            foreach (var p in separators.EnumerateObject())
            {
                string? path = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()
                    : p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("path", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (!string.IsNullOrWhiteSpace(path)) routed.Add(p.Name);
            }
        return new()
        {
            ProfileSpecificMods = settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("profile_specific_mods", out var specific) && specific.ValueKind == JsonValueKind.True,
            // Longest first, as Amethyst applies them.
            StripPrefixes = Lists(Key("mod_strip_prefixes", "mod_strip_prefixes.json"))
                .ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.Select(Key0).Where(s => s.Length > 0).Distinct().OrderByDescending(s => s.Length).ToList(), StringComparer.Ordinal),
            Excluded = Lists(Key("excluded_mod_files", "excluded_mod_files.json"))
                .ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value.Select(Key0).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal),
            RootFiles = Lists(Key("root_mod_files", null))
                .ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value.Select(Key0).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal),
            RoutedSeparators = routed,
        };
    }

    /// <summary>The file rules of one mod as the virtual Data folder applies them, or null when the mod's files land as they are.</summary>
    public AmethystLayerRules? For(string mod, string modFolder)
    {
        var strips = StripPrefixes.GetValueOrDefault(mod) ?? [];
        var skip = new HashSet<string>(StringComparer.Ordinal);
        if (Excluded.GetValueOrDefault(mod) is { } excluded) skip.UnionWith(excluded);
        if (RootFiles.GetValueOrDefault(mod) is { } root) skip.UnionWith(root);
        // Amethyst strips a top-level Data folder from every mod (the game's post-install strip prefix).
        bool dataFolder = Directory.Exists(modFolder) && Directory.EnumerateDirectories(modFolder).Any(d => Path.GetFileName(d).Equals("data", StringComparison.OrdinalIgnoreCase));
        if (strips.Count == 0 && skip.Count == 0 && !dataFolder) return null;
        var all = strips.Concat(dataFolder ? ["data"] : []).Distinct().OrderByDescending(s => s.Length).ToList();
        return new(all, skip);
    }
}

/// <summary>Folder prefixes removed from a layer's paths (longest first) and its own paths left out, lower case with forward slashes.</summary>
public sealed record AmethystLayerRules(IReadOnlyList<string> Strip, IReadOnlySet<string> Skip)
{
    /// <summary>The Data path of a file at <paramref name="raw"/> in the mod, or null when it does not land in Data.</summary>
    public string? Map(string raw)
    {
        if (Skip.Contains(raw)) return null;
        foreach (string s in Strip)
            if (raw == s) return null;
            else if (raw.StartsWith(s + "/", StringComparison.Ordinal)) return raw[(s.Length + 1)..];
        return raw;
    }

    /// <summary>The mod's own paths that can land at Data path <paramref name="mapped"/>.</summary>
    public IEnumerable<string> Sources(string mapped)
    {
        foreach (string s in Strip) yield return s + "/" + mapped;
        yield return mapped;
    }
}
