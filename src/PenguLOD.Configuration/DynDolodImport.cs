using System.Globalization;
using System.Text.Json;
using PenguLOD.Desktop.Models;

namespace PenguLOD.Desktop.Services;

/// <summary>
/// Reads the choices saved in a DynDOLOD or TexGen preset (<c>Edit Scripts\DynDOLOD\Presets\*.ini</c>) and sets the
/// PenguLOD settings that mean the same. Settings without a counterpart are listed, not guessed.
/// </summary>
public static class DynDolodImport
{
    /// <param name="Kind">"DynDOLOD" or "TexGen".</param>
    /// <param name="Changes">One line per setting that changed, with the old and new value.</param>
    /// <param name="Unchanged">Settings the file names that already had that value.</param>
    /// <param name="NotImported">Keys of the file with a choice PenguLOD has no counterpart for.</param>
    /// <param name="MeshRules">Mesh rules the file holds. They are not imported.</param>
    public sealed record Result(string Kind, IReadOnlyList<string> Changes, int Unchanged, IReadOnlyList<string> NotImported, int MeshRules);

    // Choices of a DynDOLOD preset that are read here or have no counterpart. Every other key of its [DynDOLOD] section
    // with the value 0 or 1 is a worldspace.
    private static readonly string[] DynDolodKeys =
    [
        "OutputPath", "Preset", "PresetCopyChild", "DoObjectLOD", "Level32", "DoTreeLOD", "UltraTreeLOD", "TreeLargeRef", "DoDynamicLOD",
        "DoOcclusion", "DoGrass", "DoTerrainUnderside", "DoSeasons", "SeasonsSnow", "SeasonsToGenerate", "LoadCandles", "LoadFXGlow",
        "AutoMap", "FakeLights", "FakeLightsChildWorld", "Windows", "WindowsHigh", "UseOrgLOD", "UpgradeLargeReferences",
        "WorkaroundLargeReferencesBugs", "DowngradeNormalReferences", "CopyParent", "TreeLODMaxTileSize", "TreeLODBrightness",
        "GrassLODMaxTileSize", "GrassLODBrightness", "ObjectLODMaxTileSize", "ObjectLODBrightness", "FullTextureMaxTileSize",
        "FullTextureBrightness", "OcclusionPlugin", "OcclusionQuality", "GrassLODDensity", "GrassLODMode", "TerrainUndersideQuality",
        "TerrainUndersideHeight", "LODLevel", "LODX", "LODY", "FarGrid", "NearGrid",
    ];

    // Choices that change the output and have no PenguLOD counterpart; reported when the file sets them.
    private static readonly string[] DynDolodNotImported =
    [
        "TreeLargeRef", "SeasonsSnow", "FakeLights", "WindowsHigh", "UpgradeLargeReferences", "DowngradeNormalReferences",
        "TreeLODMaxTileSize", "TreeLODBrightness", "GrassLODMaxTileSize", "GrassLODBrightness", "ObjectLODBrightness",
        "FullTextureBrightness", "GrassLODMode", "TerrainUndersideHeight",
    ];

    private static readonly string[] TexGenNotImported =
    [
        "RenderObjects", "RenderObjectsSize", "BillboardsMinSize", "StitchPBRScale", "BillboardPBRScale",
        "GrassBillboardsDirect", "GrassBillboardsAmbient", "GrassHDBillboardsDirect", "GrassHDBillboardsAmbient",
    ];

    /// <summary>Reads the file and applies what it holds. Nothing is changed when the file is not a DynDOLOD or TexGen preset.</summary>
    public static Result Apply(DesktopSettings settings, string file)
    {
        var (sections, order) = ReadIni(file);
        var before = settings.Snapshot();
        var preset = new QualityPreset { Name = Path.GetFileName(file) };
        string? worldspaces = null;
        string kind; IEnumerable<string> notImported; int rules = 0;

        if (sections.TryGetValue("TexGen", out var texGen))
        {
            kind = "TexGen";
            TexGen(texGen, preset);
            notImported = TexGenNotImported.Where(k => Set(texGen, k));
        }
        else if (sections.TryGetValue("DynDOLOD", out var dynDolod) && dynDolod.ContainsKey("DoObjectLOD"))
        {
            kind = "DynDOLOD";
            worldspaces = DynDolod(dynDolod, order["DynDOLOD"], preset);
            notImported = DynDolodNotImported.Where(k => Set(dynDolod, k));
            rules = sections.Where(s => s.Key.EndsWith("LODGen", StringComparison.OrdinalIgnoreCase))
                .Sum(s => s.Value.Keys.Count(k => k.StartsWith("LODGen", StringComparison.OrdinalIgnoreCase)));
        }
        else throw new InvalidDataException("This is not a DynDOLOD or TexGen preset. Choose a file from DynDOLOD's Edit Scripts\\DynDOLOD\\Presets folder.");

        var changes = QualityPresets.Apply(settings, preset).ToList();
        int unchanged = preset.Options.Count + preset.Settings.Count - changes.Count;
        if (worldspaces is not null)
        {
            if (before.Worldspaces == worldspaces) unchanged++; else changes.Add($"Worldspaces: {before.Worldspaces} → {worldspaces}");
            settings.Worldspaces = worldspaces;
        }
        return new Result(kind, changes, unchanged, notImported.ToArray(), rules);
    }

    // A choice left at 0, empty or Default is DynDOLOD's own default and needs no mention.
    private static bool Set(Dictionary<string, string> ini, string key) => ini.TryGetValue(key, out var v) && v is not ("" or "0" or "Default");

    private static string? DynDolod(Dictionary<string, string> ini, List<string> order, QualityPreset preset)
    {
        void Option(string key, object value) => preset.Options[key] = JsonSerializer.SerializeToElement(value, value.GetType());
        bool? Flag(string key) => ini.TryGetValue(key, out var v) ? v.Trim() switch { "1" => true, "0" => false, _ => null } : null;

        if (ini.TryGetValue("Preset", out var rules) && new[] { "Low", "Medium", "High" }.FirstOrDefault(p => p.Equals(rules.Trim(), StringComparison.OrdinalIgnoreCase)) is { } level)
            Option("preset", level);
        if (Flag("Level32") is { } lod32) Option("lod32", lod32);
        // DynDOLOD's Ultra puts trees into object LOD with the 3D models its rules name; Tree LOD alone is billboards only.
        if (Flag("UltraTreeLOD") is { } ultra)
        {
            Option("tree3d", ultra);
            Option("trees", ultra || Flag("DoTreeLOD") != false);
        }
        else if (Flag("DoTreeLOD") is { } trees) Option("trees", trees);
        if (Flag("DoDynamicLOD") is { } dynamic) { Option("dynamic", dynamic); Option("grid", dynamic); }
        if (Flag("DoOcclusion") is { } occlusion) Option("occlusion", occlusion);
        if (Flag("DoGrass") is { } grass) Option("grass", grass);
        if (ini.TryGetValue("GrassLODDensity", out var density) && Number(density) is { } percent and >= 0 and <= 100) Option("grassDensity", percent / 100);
        if (Flag("DoTerrainUnderside") is { } underside) Option("underside", underside);
        if (Set(ini, "TerrainUndersideQuality") && Number(ini["TerrainUndersideQuality"]) is { } uq)
        {
            string step = (int)uq switch
            {
                8 or 16 or 32 or 64 or 128 => ((int)uq).ToString(CultureInfo.InvariantCulture),
                >= 10 => "8",
                >= 7 => "16",
                >= 4 => "32",
                >= 2 => "64",
                >= 1 => "128",
                _ => "16",
            };
            Option("undersideStep", step);
        }
        if (Flag("DoSeasons") is { } seasons) Option("seasons", seasons);
        if (Set(ini, "SeasonsToGenerate") && TryParseSeasons(ini["SeasonsToGenerate"], out var sWin, out var sSpr, out var sSum, out var sAut))
        {
            Option("seasonWinter", sWin);
            Option("seasonSpring", sSpr);
            Option("seasonSummer", sSum);
            Option("seasonAutumn", sAut);
        }
        if (Flag("LoadCandles") is { } candles) Option("candles", candles);
        if (Flag("LoadFXGlow") is { } glow) Option("fxGlow", glow);
        if (Flag("Windows") is { } windows) Option("glowWindows", windows);
        if (Flag("CopyParent") is { } parent) Option("parentCopies", parent);
        // Quality 1, 2 and 3 compare 1, 4 and 16 sample points per cell.
        if (ini.TryGetValue("OcclusionQuality", out var quality) && Number(quality) is { } q and (1 or 2 or 3)) Option("occSamples", q == 3 ? 4m : q);
        // DynDOLOD caps the textures it puts on the object atlas: LOD textures at one size, full textures at another.
        if (ini.TryGetValue("ObjectLODMaxTileSize", out var lodTile) && Size(lodTile, 128, 4096) is { } lodSize) Option("objectTextureSize", lodSize);
        if (ini.TryGetValue("FullTextureMaxTileSize", out var fullTile) && Size(fullTile, 64, 512) is { } fullSize) Option("atlasCell", fullSize);

        // The worldspace list follows the choices, one "EditorID=0|1" line each.
        int last = order.FindLastIndex(k => DynDolodKeys.Contains(k, StringComparer.OrdinalIgnoreCase));
        var selected = order.Skip(last + 1).Where(k => ini[k] == "1").ToArray();
        return selected.Length > 0 ? string.Join(", ", selected) : null;
    }

    private static void TexGen(Dictionary<string, string> ini, QualityPreset preset)
    {
        void Setting(string name, object value) => preset.Settings[name] = JsonSerializer.SerializeToElement(value, value.GetType());
        bool? Flag(string key) => ini.TryGetValue(key, out var v) ? v.Trim() switch { "1" => true, "0" => false, _ => null } : null;
        decimal? Value(string key) => ini.TryGetValue(key, out var v) ? Number(v) : null;
        static bool PowerOfTwo(decimal v, int min, int max) => v == decimal.Truncate(v) && v >= min && v <= max && ((int)v & ((int)v - 1)) == 0;

        if (Flag("StitchObjects") is { } stitched) Setting(nameof(DesktopSettings.TextureGenStitched), stitched);
        if (Value("StitchObjectsSize") is { } baseSize && PowerOfTwo(baseSize, 64, 1024)) Setting(nameof(DesktopSettings.TextureGenBaseSize), baseSize);
        bool? treeHd = Flag("TreeHDBillboards"), grassHd = Flag("GrassHDBillboards");
        if (Flag("TreeBillboards") is { } trees) Setting(nameof(DesktopSettings.TextureGenTrees), trees || treeHd == true);
        if (Flag("GrassBillboards") is { } grass) Setting(nameof(DesktopSettings.TextureGenGrass), grass || grassHd == true);
        if (treeHd is not null || grassHd is not null) Setting(nameof(DesktopSettings.TextureGenHdViews), treeHd == true || grassHd == true);
        if (Flag("RenderBillboards") is { } trunks) Setting(nameof(DesktopSettings.TextureGenTrunks), trunks);
        if (Value("BillboardsMaxSize") is { } maxSize && PowerOfTwo(maxSize, 32, 2048)) Setting(nameof(DesktopSettings.TextureGenMaxSize), maxSize);
        // TexGen stores units per pixel in tenths: 80 is 8.0.
        if (Value("BillboardsUnitsPerPixel") is { } tenths and >= 3 and <= 1280) Setting(nameof(DesktopSettings.TextureGenUnitsPerPixel), tenths / 10);

        // TexGen stores light as a percentage. HD tree billboards have their own pair, used when they are on.
        string tree = treeHd == true ? "TreeHDBillboards" : "TreeBillboards";
        void Light(string key, string name) { if (Value(key) is { } v and >= 0 and <= 400) Setting(name, v / 100); }
        Light(tree + "Ambient", nameof(DesktopSettings.TextureGenTreeAmbient));
        Light(tree + "Direct", nameof(DesktopSettings.TextureGenTreeDirect));
        Light("RenderBillboardsAmbient", nameof(DesktopSettings.TextureGenTrunkAmbient));
        Light("RenderBillboardsDirect", nameof(DesktopSettings.TextureGenTrunkDirect));
        if (Value("RenderPBRScale") is { } pbr and >= .1m and <= 4) Setting(nameof(DesktopSettings.BakePbrScale), pbr);
    }

    /// <summary>A power-of-two size as the choice text of an option, brought into the option's range.</summary>
    private static string? Size(string text, int min, int max) =>
        Number(text) is { } v && v == decimal.Truncate(v) && v >= 1 && v <= 65536 && ((int)v & ((int)v - 1)) == 0
            ? Math.Clamp((int)v, min, max).ToString(CultureInfo.InvariantCulture) : null;

    private static bool TryParseSeasons(string text, out bool winter, out bool spring, out bool summer, out bool autumn)
    {
        winter = spring = summer = autumn = false;
        if (Number(text) is { } mask && mask == decimal.Truncate(mask) && mask is >= 1 and <= 15)
        {
            int bits = (int)mask;
            winter = (bits & 1) != 0; spring = (bits & 2) != 0; summer = (bits & 4) != 0; autumn = (bits & 8) != 0;
            return true;
        }
        foreach (var token in text.Split([',', ';', '|', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var t = token.ToLowerInvariant();
            if (t.StartsWith("win", StringComparison.Ordinal)) winter = true;
            else if (t.StartsWith("spr", StringComparison.Ordinal)) spring = true;
            else if (t.StartsWith("sum", StringComparison.Ordinal)) summer = true;
            else if (t.StartsWith("aut", StringComparison.Ordinal) || t.StartsWith("fal", StringComparison.Ordinal)) autumn = true;
        }
        return winter || spring || summer || autumn;
    }

    private static decimal? Number(string text) =>
        decimal.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>Sections of an INI file by name, each a key-to-value map, and each section's keys in file order. Comment lines start with ';' or '/'.</summary>
    private static (Dictionary<string, Dictionary<string, string>> Sections, Dictionary<string, List<string>> Order) ReadIni(string file)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var order = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null; List<string>? keys = null;
        foreach (string raw in File.ReadLines(file))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '/') continue;
            if (line[0] == '[')
            {
                string name = line.Trim('[', ']').Trim();
                if (!sections.TryGetValue(name, out current)) { sections[name] = current = new(StringComparer.OrdinalIgnoreCase); order[name] = []; }
                keys = order[name];
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0 || current is null) continue;
            string key = line[..eq].Trim();
            if (!current.ContainsKey(key)) keys!.Add(key);
            current[key] = line[(eq + 1)..].Trim();
        }
        return (sections, order);
    }
}
