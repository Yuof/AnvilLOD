using System.Text.Json;
using PenguLOD.Core.Settings;
using PenguLOD.Desktop.Models;

namespace PenguLOD.Desktop.Services;

/// <summary>
/// A named set of generation settings: Object LOD options by key, and Textures, Terrain and Grass cache settings by
/// property name. Applying one sets what it names and leaves every other setting as it is.
/// </summary>
public sealed class QualityPreset
{
    public int PresetVersion { get; set; } = 1;
    public string Name { get; set; } = "";
    public Dictionary<string, JsonElement> Options { get; set; } = [];
    public Dictionary<string, JsonElement> Settings { get; set; } = [];
}

/// <summary>
/// The built-in High, Balanced and Performance presets, and presets saved to files. The built-in ones hold the values
/// DynDOLOD and TexGen ship with, for the settings that have a counterpart there. A preset never holds a path, the game
/// source, a worldspace selection or a machine preference, so a file from another setup is safe to load.
/// </summary>
public static class QualityPresets
{
    public const string High = "High", Balanced = "Balanced", Performance = "Performance";
    public static readonly IReadOnlyList<string> BuiltInNames = [High, Balanced, Performance];

    /// <summary>Options that describe the installed game, not the output. No preset sets them.</summary>
    public static readonly IReadOnlySet<string> InstallOptions = new HashSet<string>(StringComparer.Ordinal) { "skse", "esl" };

    /// <summary>The settings outside the Object LOD options that a saved preset holds.</summary>
    public static readonly IReadOnlySet<string> SettingNames = new HashSet<string>(StringComparer.Ordinal)
    {
        // Textures
        nameof(DesktopSettings.TextureGenStitched), nameof(DesktopSettings.TextureGenTrees), nameof(DesktopSettings.TextureGenTrunks),
        nameof(DesktopSettings.TextureGenGrass), nameof(DesktopSettings.TextureGenObjects),
        nameof(DesktopSettings.TextureGenBaseSize), nameof(DesktopSettings.TextureGenMaxSize),
        nameof(DesktopSettings.TextureGenUnitsPerPixel), nameof(DesktopSettings.TextureGenGrassUnitsPerPixel), nameof(DesktopSettings.TextureGenTrunkUnitsPerPixel),
        nameof(DesktopSettings.TextureGenHdViews), nameof(DesktopSettings.TextureGenReferenceScale), nameof(DesktopSettings.TextureGenUnplacedTreeScale),
        nameof(DesktopSettings.TextureGenComplexThreshold), nameof(DesktopSettings.TextureGenGrassTextures), nameof(DesktopSettings.TextureGenBrightness),
        nameof(DesktopSettings.TextureGenMinTreeHeight), nameof(DesktopSettings.TextureGenMinGrassHeight),
        nameof(DesktopSettings.BakeSourceNormals), nameof(DesktopSettings.BakePbr), nameof(DesktopSettings.BakePbrScale),
        nameof(DesktopSettings.BakePbrGamma), nameof(DesktopSettings.BakeAo),
        nameof(DesktopSettings.TextureGenTreeAmbient), nameof(DesktopSettings.TextureGenTreeDirect),
        nameof(DesktopSettings.TextureGenGrassAmbient), nameof(DesktopSettings.TextureGenGrassDirect),
        nameof(DesktopSettings.TextureGenTrunkAmbient), nameof(DesktopSettings.TextureGenTrunkDirect),
        // Terrain
        nameof(DesktopSettings.TerrainTextureSize), nameof(DesktopSettings.TerrainBrightness), nameof(DesktopSettings.TerrainVertexColor),
        nameof(DesktopSettings.TerrainNormalStrength), nameof(DesktopSettings.TerrainPbr), nameof(DesktopSettings.TerrainPbrTwins),
        nameof(DesktopSettings.TerrainPbrScale), nameof(DesktopSettings.TerrainPbrGamma), nameof(DesktopSettings.TerrainAo),
        nameof(DesktopSettings.TerrainRepeats), nameof(DesktopSettings.TerrainSkirt), nameof(DesktopSettings.TerrainWater),
        nameof(DesktopSettings.TerrainFallback), nameof(DesktopSettings.TerrainFillUncovered), nameof(DesktopSettings.TerrainSubstituteTextures),
        nameof(DesktopSettings.TerrainHeightmaps), nameof(DesktopSettings.TerrainHeightmapSize),
        // Grass cache
        nameof(DesktopSettings.GrassPlacement), nameof(DesktopSettings.GrassRejection), nameof(DesktopSettings.GrassSeasons),
        nameof(DesktopSettings.GrassMinSize), nameof(DesktopSettings.GrassRenderGeometry),
    };

    private static readonly IReadOnlySet<string> NotInPresets = typeof(DesktopSettings).GetProperties()
        .Select(p => p.Name).Where(n => !SettingNames.Contains(n)).ToHashSet(StringComparer.Ordinal);

    // What DynDOLOD and TexGen ship, as (key, Performance, Balanced, High): DynDOLOD's Low, Medium and High mesh rules, and
    // the sizes the two programs choose for a 1080p, 1440p and 2160p game resolution. The rest is the same in all three.
    private static readonly (string Key, object Performance, object Balanced, object High)[] OptionLevels =
    [
        ("preset", "Low", "Medium", "High"),
        // DynDOLOD "Max tile size full" (a quarter of the screen height) and "Max tile size LOD" (the screen height).
        ("atlasCell", "256", "256", "512"),
        ("objectTextureSize", "1024", "1024", "2048"),
        // DynDOLOD occlusion quality 2 (4 sample points per cell) and OcclusionRadius.
        ("occSamples", 2m, 2m, 2m),
        ("occRadius", 100m, 100m, 100m),
    ];

    private static readonly (string Name, object Performance, object Balanced, object High)[] SettingLevels =
    [
        // TexGen sets these three from the game resolution on its first run.
        (nameof(DesktopSettings.TextureGenBaseSize), 256m, 256m, 512m),
        (nameof(DesktopSettings.TextureGenUnitsPerPixel), 11.8m, 8.85m, 5.9m),
        (nameof(DesktopSettings.TextureGenMaxSize), 1024m, 1024m, 2048m),
        // TexGen_SSE.INI: MinTreeModelHeight, MinGrassModelHeight, TreeModelHeightMultiplier.
        (nameof(DesktopSettings.TextureGenMinTreeHeight), 256m, 256m, 256m),
        (nameof(DesktopSettings.TextureGenMinGrassHeight), 32m, 32m, 32m),
        (nameof(DesktopSettings.TextureGenUnplacedTreeScale), 1.7m, 1.7m, 1.7m),
        // TexGen's light for tree and rendered (trunk) billboards, and its PBR scale.
        (nameof(DesktopSettings.TextureGenTreeAmbient), .6m, .6m, .6m),
        (nameof(DesktopSettings.TextureGenTreeDirect), 1.35m, 1.35m, 1.35m),
        (nameof(DesktopSettings.TextureGenTrunkAmbient), 1.5m, 1.5m, 1.5m),
        (nameof(DesktopSettings.TextureGenTrunkDirect), .5m, .5m, .5m),
        (nameof(DesktopSettings.BakePbrScale), .65m, .65m, .65m),
    ];

    public static bool IsBuiltIn(string name) => BuiltInNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    public static QualityPreset BuiltIn(string name)
    {
        string canonical = BuiltInNames.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown preset '{name}' (use High, Balanced or Performance).");
        object Pick(object performance, object balanced, object high) =>
            canonical == Performance ? performance : canonical == Balanced ? balanced : high;
        var preset = new QualityPreset { Name = canonical };
        foreach (var (key, p, b, h) in OptionLevels) preset.Options[key] = Element(Pick(p, b, h));
        foreach (var (property, p, b, h) in SettingLevels) preset.Settings[property] = Element(Pick(p, b, h));
        return preset;
    }

    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, value.GetType());

    /// <summary>Every generation setting a preset may hold, at its current value.</summary>
    public static QualityPreset Capture(DesktopSettings settings, string name)
    {
        var preset = new QualityPreset { Name = name, Settings = PresetSnapshot.Capture(settings, NotInPresets) };
        foreach (var spec in GenerationOptions.All.Where(o => !InstallOptions.Contains(o.Key)))
            preset.Options[spec.Key] = settings.Options.TryGetValue(spec.Key, out var value) ? value.Clone() : Element(spec.Default);
        return preset;
    }

    /// <summary>
    /// Sets what the preset names. Options this build does not know, install options and settings outside
    /// <see cref="SettingNames"/> are skipped. Nothing is changed when a value is not valid for its setting.
    /// </summary>
    public static void Apply(DesktopSettings settings, QualityPreset preset)
    {
        var known = GenerationOptions.All.Select(o => o.Key).Where(k => !InstallOptions.Contains(k)).ToHashSet(StringComparer.Ordinal);
        var options = preset.Options.Where(o => known.Contains(o.Key)).ToArray();
        var values = preset.Settings.Where(s => SettingNames.Contains(s.Key)).ToDictionary(s => s.Key, s => s.Value);

        var trial = settings.Snapshot();
        foreach (var (key, value) in options) trial.Options[key] = value;
        PresetSnapshot.Apply(trial, values, NotInPresets);
        try { GenerationOptions.Validate(trial); }
        catch (ArgumentException e) { throw new InvalidDataException($"Preset '{preset.Name}' was not applied. {e.Message}", e); }

        foreach (var (key, value) in options)
            if (!settings.Options.TryGetValue(key, out var current) || !JsonElement.DeepEquals(current, value)) settings.SetOption(key, value.Clone());
        PresetSnapshot.Apply(settings, values, NotInPresets);
    }

    /// <summary>The built-in preset whose every value the settings have, or null.</summary>
    public static string? Matching(DesktopSettings settings)
    {
        var current = Capture(settings, "");
        return BuiltInNames.FirstOrDefault(name =>
        {
            var preset = BuiltIn(name);
            return preset.Options.All(o => Same(current.Options[o.Key], o.Value)) && preset.Settings.All(s => Same(current.Settings[s.Key], s.Value));
        });
    }

    // 256 and 256.0 are the same setting.
    private static bool Same(JsonElement a, JsonElement b) =>
        a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number ? a.GetDecimal() == b.GetDecimal() : JsonElement.DeepEquals(a, b);

    /// <summary>
    /// Reads a preset file. An exported settings file is accepted too: its generation settings become the preset, and its
    /// paths, game source and worldspaces are left behind.
    /// </summary>
    public static QualityPreset Read(string file)
    {
        string text = File.ReadAllText(file);
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The file is not a preset.");
            if (root.TryGetProperty(nameof(QualityPreset.PresetVersion), out var version))
            {
                if (version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1) throw new InvalidDataException("Unsupported preset version.");
                var preset = JsonSerializer.Deserialize<QualityPreset>(text)!;
                preset.Options ??= []; preset.Settings ??= [];
                if (string.IsNullOrWhiteSpace(preset.Name)) preset.Name = Path.GetFileNameWithoutExtension(file);
                return preset;
            }
            if (root.TryGetProperty(nameof(DesktopSettings.SchemaVersion), out _))
                return Capture(SettingsStore.Read(file), Path.GetFileNameWithoutExtension(file));
            throw new InvalidDataException("The file is not a preset.");
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        { throw new InvalidDataException("The preset could not be read: " + e.Message, e); }
    }

    public static void Write(string file, QualityPreset preset)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(preset, SettingsStore.JsonOptions));
            File.Move(temp, file, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    /// <summary>Names of the presets saved in a folder, in order.</summary>
    public static IReadOnlyList<string> Saved(string folder) => !Directory.Exists(folder) ? []
        : Directory.EnumerateFiles(folder, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>()
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>The file a saved preset of this name has. The name must work as a file name and not be a built-in one.</summary>
    public static string SavedFile(string folder, string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("Enter a name for the preset.");
        if (name.Length > 80 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(['/', '\\', ':']) >= 0)
            throw new ArgumentException("Use a preset name that works as a file name, without slashes or colons.");
        if (IsBuiltIn(name)) throw new ArgumentException($"{name} is a built-in preset. Choose another name.");
        return Path.Combine(folder, name + ".json");
    }
}
