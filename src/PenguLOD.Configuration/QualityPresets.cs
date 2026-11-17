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

    /// <summary>Screen heights the presets can be made for. TexGen and DynDOLOD choose their texture sizes from the game's resolution.</summary>
    public static readonly IReadOnlyList<int> ScreenHeights = [720, 1080, 1440, 2160, 4320];

    /// <summary>
    /// The sizes TexGen and DynDOLOD choose for a game running at a screen height, by the rule in the programs themselves (DynDOLOD 3
    /// Alpha-219). A base size comes from the height: 128 below 900, 256 below 1800, 512 below 3600, else 1024. Stitched and rendered
    /// object textures and a full texture on the atlas get the base size; billboards and LOD textures on the atlas four times it. Units
    /// per pixel is 12750 / height less 0.4, cut to a whole number, plus a half above 1440: 11 at 1080p, 8 at 1440p, 5.5 at 2160p.
    /// </summary>
    public sealed record ScreenSizes(decimal UnitsPerPixel, int BaseSize, int MaxBillboard, int AtlasCell, int LodTexture)
    {
        public static ScreenSizes For(int height)
        {
            if (height is < 200 or > 32768) throw new ArgumentOutOfRangeException(nameof(height), "The screen height must be between 200 and 32768.");
            int baseSize = height < 900 ? 128 : height < 1800 ? 256 : height < 3600 ? 512 : 1024;
            decimal units = decimal.Truncate(12750m / height - .4m) + (height > 1440 ? .5m : 0m);
            return new(Math.Clamp(units, .25m, 128m), baseSize, Math.Clamp(baseSize * 4, 32, 2048), Math.Clamp(baseSize, 64, 512), Math.Clamp(baseSize * 4, 128, 4096));
        }
    }

    // The same in every preset: TexGen_SSE.INI (minimum model heights, tree height multiplier), TexGen's light for tree and rendered
    // (trunk) billboards and its PBR scale, and DynDOLOD's occlusion quality 2 (4 sample points per cell), radius and observer height.
    private static readonly (string Key, object Value)[] ShippedOptions =
    [
        ("occSamples", 2m), ("occRadius", 100m), ("occEye", 0m),
    ];

    private static readonly (string Name, object Value)[] ShippedSettings =
    [
        (nameof(DesktopSettings.TextureGenMinTreeHeight), 256m),
        (nameof(DesktopSettings.TextureGenMinGrassHeight), 32m),
        (nameof(DesktopSettings.TextureGenUnplacedTreeScale), 1.7m),
        (nameof(DesktopSettings.TextureGenTreeAmbient), .55m),
        (nameof(DesktopSettings.TextureGenTreeDirect), 1.35m),
        (nameof(DesktopSettings.TextureGenTrunkAmbient), 1.5m),
        (nameof(DesktopSettings.TextureGenTrunkDirect), .5m),
        (nameof(DesktopSettings.BakePbrScale), .65m),
    ];

    // What a new DynDOLOD and TexGen have ticked, for the choices that do not depend on what is installed.
    private static readonly (string Key, bool Value)[] ShippedTicks =
    [
        ("trees", true), ("tree3d", false), ("dynamic", true), ("grid", true), ("occlusion", true), ("grass", false), ("lod32", false),
        ("candles", false), ("fxGlow", false), ("glowWindows", false), ("parentCopies", false),
    ];

    private static readonly (string Name, bool Value)[] ShippedTextureKinds =
    [
        (nameof(DesktopSettings.TextureGenStitched), true), (nameof(DesktopSettings.TextureGenTrees), true),
        (nameof(DesktopSettings.TextureGenTrunks), true), (nameof(DesktopSettings.TextureGenGrass), false),
    ];

    public const string DynDolodDefaults = "DynDOLOD defaults";

    /// <summary>
    /// The state of a new DynDOLOD and TexGen: the Balanced preset (DynDOLOD starts on its Medium rules), and what the two have
    /// ticked. Terrain underside and seasons are not set: DynDOLOD ticks them by what is installed.
    /// </summary>
    public static QualityPreset Shipped(int screenHeight)
    {
        var preset = BuiltIn(Balanced, screenHeight);
        preset.Name = DynDolodDefaults;
        foreach (var (key, value) in ShippedTicks) preset.Options[key] = Element(value);
        foreach (var (name, value) in ShippedTextureKinds) preset.Settings[name] = Element(value);
        return preset;
    }

    public static bool IsBuiltIn(string name) => BuiltInNames.Contains(name, StringComparer.OrdinalIgnoreCase) || name.Equals(DynDolodDefaults, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What DynDOLOD and TexGen ship with: DynDOLOD's High, Medium or Low mesh rules (High, Balanced, Performance), and the texture
    /// sizes the two programs choose for a game running at this screen height.
    /// </summary>
    public static QualityPreset BuiltIn(string name, int screenHeight)
    {
        string canonical = BuiltInNames.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown preset '{name}' (use High, Balanced or Performance).");
        var sizes = ScreenSizes.For(screenHeight);
        var preset = new QualityPreset { Name = canonical };
        preset.Options["preset"] = Element(canonical == Performance ? "Low" : canonical == Balanced ? "Medium" : "High");
        // DynDOLOD "Max tile size full" and "Max tile size LOD".
        preset.Options["atlasCell"] = Element(sizes.AtlasCell.ToString(System.Globalization.CultureInfo.InvariantCulture));
        preset.Options["objectTextureSize"] = Element(sizes.LodTexture.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var (key, value) in ShippedOptions) preset.Options[key] = Element(value);
        preset.Settings[nameof(DesktopSettings.TextureGenBaseSize)] = Element((decimal)sizes.BaseSize);
        preset.Settings[nameof(DesktopSettings.TextureGenUnitsPerPixel)] = Element(sizes.UnitsPerPixel);
        preset.Settings[nameof(DesktopSettings.TextureGenMaxSize)] = Element((decimal)sizes.MaxBillboard);
        foreach (var (property, value) in ShippedSettings) preset.Settings[property] = Element(value);
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
    /// Returns one line per setting that changed, with its old and new value.
    /// </summary>
    public static IReadOnlyList<string> Apply(DesktopSettings settings, QualityPreset preset)
    {
        var before = settings.Snapshot();
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

        var changes = new List<string>();
        foreach (var (key, value) in options)
        {
            var spec = GenerationOptions.All.Single(o => o.Key == key);
            var old = before.Options.TryGetValue(key, out var o) ? o : Element(spec.Default);
            if (!Same(old, value)) changes.Add($"{spec.Label}: {Show(old)} → {Show(value)}");
        }
        foreach (var (name, value) in values)
        {
            var property = typeof(DesktopSettings).GetProperty(name)!;
            var old = JsonSerializer.SerializeToElement(property.GetValue(before), property.PropertyType);
            if (!Same(old, value)) changes.Add($"{SettingLabels.GetValueOrDefault(name, name)}: {Show(old)} → {Show(value)}");
        }
        return changes;
    }

    /// <summary>How the desktop labels the settings a preset or an import changes, for the list of changes.</summary>
    internal static readonly Dictionary<string, string> SettingLabels = new(StringComparer.Ordinal)
    {
        [nameof(DesktopSettings.TextureGenStitched)] = "Stitched object textures",
        [nameof(DesktopSettings.TextureGenBaseSize)] = "Stitch base size",
        [nameof(DesktopSettings.TextureGenTrees)] = "Tree billboards (textures)",
        [nameof(DesktopSettings.TextureGenGrass)] = "Grass billboards (textures)",
        [nameof(DesktopSettings.TextureGenTrunks)] = "Hybrid tree trunk textures",
        [nameof(DesktopSettings.TextureGenHdViews)] = "HD front and side billboards",
        [nameof(DesktopSettings.TextureGenMaxSize)] = "Maximum billboard size",
        [nameof(DesktopSettings.TextureGenUnitsPerPixel)] = "Tree world units per pixel",
        [nameof(DesktopSettings.TextureGenMinTreeHeight)] = "Minimum tree height",
        [nameof(DesktopSettings.TextureGenMinGrassHeight)] = "Minimum grass height",
        [nameof(DesktopSettings.TextureGenUnplacedTreeScale)] = "Unplaced tree resolution scale",
        [nameof(DesktopSettings.TextureGenTreeAmbient)] = "Tree ambient light",
        [nameof(DesktopSettings.TextureGenTreeDirect)] = "Tree direct light",
        [nameof(DesktopSettings.TextureGenTrunkAmbient)] = "Trunk ambient light",
        [nameof(DesktopSettings.TextureGenTrunkDirect)] = "Trunk direct light",
        [nameof(DesktopSettings.BakePbrScale)] = "PBR scale (textures)",
    };

    internal static string Show(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => "on", JsonValueKind.False => "off",
        JsonValueKind.Number => value.GetDecimal().ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
        _ => value.GetString() ?? "",
    };

    /// <summary>The built-in preset and screen height whose every value the settings have, or null.</summary>
    public static (string Name, int ScreenHeight)? Matching(DesktopSettings settings, IEnumerable<int>? screenHeights = null)
    {
        var current = Capture(settings, "");
        foreach (int height in (screenHeights ?? ScreenHeights).Distinct())
            foreach (string name in BuiltInNames)
            {
                var preset = BuiltIn(name, height);
                if (preset.Options.All(o => Same(current.Options[o.Key], o.Value)) && preset.Settings.All(s => Same(current.Settings[s.Key], s.Value)))
                    return (name, height);
            }
        return null;
    }

    // 256 and 256.0 are the same setting.
    internal static bool Same(JsonElement a, JsonElement b) =>
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
