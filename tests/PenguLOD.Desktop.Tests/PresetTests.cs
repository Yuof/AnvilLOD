using System.Text.Json;
using PenguLOD.Desktop.Models;
using PenguLOD.Desktop.Services;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace PenguLOD.Desktop.Tests;

public sealed class PresetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PenguLOD-preset-tests-" + Guid.NewGuid().ToString("N"));
    public PresetTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private MainWindow Window() => new(new SettingsStore(Path.Combine(_root, "settings.json")));
    private static void Option(DesktopSettings s, string key, object value) => s.SetOption(key, JsonSerializer.SerializeToElement(value, value.GetType()));
    private static T Option<T>(DesktopSettings s, string key) => GenerationOptions.Get<T>(s, key);

    // Settings that are not part of a preset: paths, the game source, worldspaces, run and machine choices, and the tools.
    private static readonly string[] LeftOut =
    [
        "SchemaVersion", "Options", "BaseFolder", "Seasons", "SeasonWinter", "SeasonSpring", "SeasonSummer", "SeasonAutumn",
        "SourceMode", "Edition", "Instance", "Profile", "WinePrefix", "GameFolder", "Mo2GameFolder", "ModsFolder", "ProfilesFolder",
        "OverwriteFolder", "PluginsFile", "GameIni", "OutputFolder", "ResourceFolders", "DynDolodFolder", "CustomRulesFile", "Worldspaces",
        "CacheFolder", "MakerInputs", "MakerOutput", "MakerGroup", "MakerPlugin", "MakerLevels", "MakerBudget", "MakerCustomBudget",
        "MakerDetail", "MakerMinSize", "MakerOverwrite", "MakerRules", "AuthorPlugin", "AuthorOutput", "AuthorMeshes", "AuthorBillboards",
        "AuthorRules", "AuthorStandIns", "AuthorOverwrite", "AuthorMinSize", "AuthorTreeHeight", "PipelinePreflight", "DiagnosticDeepScan",
        "DiagnosticVerifyFiles", "DiagnosticAsset", "TerrainOutput", "PipelineTerrain", "UseGeneratedTerrain", "TextureGenOutput",
        "TextureGenFilter", "UseGeneratedTextures", "GrassOutput", "GrassExecutable", "GrassWorldspaces", "GrassNgioConfig", "GrassPreview",
        "UseGeneratedGrass", "GrassCellX", "GrassCellY", "GrassRadius", "GrassWriters", "TextureInputs", "TextureOutput", "TexconvPath",
        "ConvertSource", "ConvertOutput", "CleanOutput", "TextureAllMods", "ConvertAllMeshes", "ConvertOverwrite", "TextureCompute",
        "MaxThreads", "ResumeCompletedStages", "ExpandOutputScope", "KeepPreviousOutputs", "VerifySourceContents", "PipelineGrass",
        "PipelineTextures", "PipelineLod",
    ];

    [Fact]
    public void EverySettingIsEitherHeldByAPresetOrNamedAsLeftOut()
    {
        var all = typeof(DesktopSettings).GetProperties().Select(p => p.Name).ToArray();
        Assert.Empty(QualityPresets.SettingNames.Intersect(LeftOut));
        // A new setting has to be put in one of the two lists: decide whether a preset may carry it to another setup.
        Assert.Empty(all.Except(QualityPresets.SettingNames).Except(LeftOut));
        Assert.Empty(QualityPresets.SettingNames.Concat(LeftOut).Except(all));
        Assert.DoesNotContain(QualityPresets.SettingNames, n => n.EndsWith("Folder") || n.EndsWith("Output") || n.Contains("Worldspace"));
    }

    [Fact]
    public void HighIsTheDefaultsAndEveryBuiltInPresetNamesTheSameValidSettings()
    {
        Assert.Equal(QualityPresets.High, QualityPresets.Matching(new DesktopSettings()));
        var high = QualityPresets.BuiltIn("high");
        foreach (string name in QualityPresets.BuiltInNames)
        {
            var preset = QualityPresets.BuiltIn(name);
            Assert.Equal(high.Options.Keys.Order(), preset.Options.Keys.Order());
            Assert.Equal(high.Settings.Keys.Order(), preset.Settings.Keys.Order());
            Assert.All(preset.Settings.Keys, k => Assert.Contains(k, QualityPresets.SettingNames));
            Assert.DoesNotContain(preset.Options.Keys, QualityPresets.InstallOptions.Contains);

            var s = new DesktopSettings();
            QualityPresets.Apply(s, preset);
            Assert.Equal(name, QualityPresets.Matching(s));
            foreach (decimal size in new[] { s.TextureGenBaseSize, s.TextureGenMaxSize, s.TerrainTextureSize })
                Assert.True(size >= 64 && ((int)size & ((int)size - 1)) == 0);
        }
        Assert.Throws<ArgumentException>(() => QualityPresets.BuiltIn("Ultra"));
    }

    [Fact]
    public void ABuiltInPresetSetsDetailLevelsAndLeavesFeaturesAndPathsAlone()
    {
        var s = new DesktopSettings { OutputFolder = "/out/lod", Worldspaces = "Tamriel, Blackreach", TextureGenGrass = true, TerrainBrightness = 1.2m };
        Option(s, "seasons", true); Option(s, "tree3d", true); Option(s, "skse", "VR");

        QualityPresets.Apply(s, QualityPresets.BuiltIn(QualityPresets.Performance));

        Assert.Equal("Low", Option<string>(s, "preset"));
        Assert.Equal(.03m, Option<decimal>(s, "grassDensity"));
        Assert.False(Option<bool>(s, "detailedShips"));
        Assert.Equal(128m, s.TextureGenBaseSize);
        Assert.Equal(12m, s.TextureGenUnitsPerPixel);
        Assert.Equal(256m, s.TerrainTextureSize);
        Assert.True(Option<bool>(s, "seasons")); Assert.True(Option<bool>(s, "tree3d")); Assert.Equal("VR", Option<string>(s, "skse"));
        Assert.True(s.TextureGenGrass); Assert.Equal(1.2m, s.TerrainBrightness);
        Assert.Equal("/out/lod", s.OutputFolder); Assert.Equal("Tamriel, Blackreach", s.Worldspaces);
        Assert.Equal(QualityPresets.Performance, QualityPresets.Matching(s));

        Option(s, "grassDensity", .2m);
        Assert.Null(QualityPresets.Matching(s));
        QualityPresets.Apply(s, QualityPresets.BuiltIn(QualityPresets.Balanced));
        Assert.Equal(QualityPresets.Balanced, QualityPresets.Matching(s));
        Assert.Equal("Medium", Option<string>(s, "preset"));
    }

    [Fact]
    public void ASavedPresetRestoresEveryGenerationSettingAndNoPathOrInstallChoice()
    {
        var source = new DesktopSettings { OutputFolder = "/a/lod", TextureGenOutput = "/a/textures", Worldspaces = "Tamriel", Instance = "/a/mo2",
            TerrainTextureSize = 1024, TextureGenGrass = true, GrassPlacement = "Random", MaxThreads = 4 };
        Option(source, "seasons", true); Option(source, "occlusion", true); Option(source, "grassDensity", .6m); Option(source, "skse", "VR");
        string file = Path.Combine(_root, "presets", "Mine.json");
        QualityPresets.Write(file, QualityPresets.Capture(source, "Mine"));

        var target = new DesktopSettings { OutputFolder = "/b/lod", Worldspaces = "DLC2SolstheimWorld", Instance = "/b/mo2" };
        Option(target, "objectAtlas", true);
        var preset = QualityPresets.Read(file);
        QualityPresets.Apply(target, preset);

        Assert.Equal("Mine", preset.Name);
        Assert.True(Option<bool>(target, "seasons")); Assert.True(Option<bool>(target, "occlusion")); Assert.Equal(.6m, Option<decimal>(target, "grassDensity"));
        Assert.False(Option<bool>(target, "objectAtlas"));    // the preset holds every option, at its default too
        Assert.Equal(1024m, target.TerrainTextureSize); Assert.True(target.TextureGenGrass); Assert.Equal("Random", target.GrassPlacement);
        Assert.Equal("None", Option<string>(target, "skse"));
        Assert.Equal("/b/lod", target.OutputFolder); Assert.Equal("", target.TextureGenOutput); Assert.Equal("DLC2SolstheimWorld", target.Worldspaces);
        Assert.Equal("/b/mo2", target.Instance); Assert.Equal(0m, target.MaxThreads);
        Assert.Equal(["Mine"], QualityPresets.Saved(Path.Combine(_root, "presets")));
        Assert.DoesNotContain("/a/", File.ReadAllText(file));
    }

    [Fact]
    public void AnExportedSettingsFileLoadsAsAPresetWithoutItsPaths()
    {
        var exported = new DesktopSettings { OutputFolder = "/a/lod", Worldspaces = "Tamriel", TextureGenMaxSize = 1024 };
        Option(exported, "preset", "Low");
        string file = Path.Combine(_root, "PenguLOD-preset.json");
        SettingsStore.Write(file, exported);

        var target = new DesktopSettings { OutputFolder = "/b/lod", Worldspaces = "*" };
        QualityPresets.Apply(target, QualityPresets.Read(file));

        Assert.Equal("Low", Option<string>(target, "preset")); Assert.Equal(1024m, target.TextureGenMaxSize);
        Assert.Equal("/b/lod", target.OutputFolder); Assert.Equal("*", target.Worldspaces);
    }

    [Fact]
    public void APresetFileCannotSetAPathAndAnInvalidOneChangesNothing()
    {
        var target = new DesktopSettings { OutputFolder = "/b/lod" };
        var preset = new QualityPreset { Name = "Edited" };
        preset.Settings["OutputFolder"] = JsonSerializer.SerializeToElement("/somewhere/else");
        preset.Settings["TerrainTextureSize"] = JsonSerializer.SerializeToElement(1024);
        preset.Options["skse"] = JsonSerializer.SerializeToElement("VR");
        preset.Options["optionOfALaterVersion"] = JsonSerializer.SerializeToElement(true);
        QualityPresets.Apply(target, preset);
        Assert.Equal("/b/lod", target.OutputFolder); Assert.Equal(1024m, target.TerrainTextureSize);
        Assert.Equal("None", Option<string>(target, "skse")); Assert.DoesNotContain("optionOfALaterVersion", target.Options.Keys);

        preset.Options["preset"] = JsonSerializer.SerializeToElement("Low");
        preset.Options["grassDensity"] = JsonSerializer.SerializeToElement(7);
        var error = Assert.Throws<InvalidDataException>(() => QualityPresets.Apply(target, preset));
        Assert.Contains("Grass density", error.Message);
        Assert.Equal("High", Option<string>(target, "preset"));

        string notAPreset = Path.Combine(_root, "other.json"); File.WriteAllText(notAPreset, "{\"Something\": 1}");
        Assert.Throws<InvalidDataException>(() => QualityPresets.Read(notAPreset));
        File.WriteAllText(notAPreset, "not json");
        Assert.Throws<InvalidDataException>(() => QualityPresets.Read(notAPreset));
        Assert.Throws<ArgumentException>(() => QualityPresets.SavedFile(_root, "High"));
        Assert.Throws<ArgumentException>(() => QualityPresets.SavedFile(_root, "a/b"));
        Assert.Throws<ArgumentException>(() => QualityPresets.SavedFile(_root, "  "));
    }

    private string DynDolodPreset()
    {
        string file = Path.Combine(_root, "Mine_SSE_Preset.ini");
        File.WriteAllText(file, """
            [DynDOLOD]
            OutputPath=Z:\mods\DynDOLOD Output\
            Preset=Medium
            PresetCopyChild=Low
            DoObjectLOD=1
            Level32=0
            DoTreeLOD=0
            UltraTreeLOD=1
            TreeLargeRef=0
            DoDynamicLOD=1
            DoOcclusion=1
            DoGrass=1
            DoTerrainUnderside=1
            DoSeasons=0
            SeasonsToGenerate=Default
            LoadCandles=1
            LoadFXGlow=1
            Windows=1
            WindowsHigh=1
            UpgradeLargeReferences=1
            CopyParent=0
            TreeLODBrightness=0
            ObjectLODMaxTileSize=2048
            OcclusionQuality=3
            GrassLODDensity=55
            GrassLODMode=1
            FarGrid=21
            NearGrid=11
            Tamriel=1
            DLC2SolstheimWorld=1
            MarkarthWorld=0
            Blackreach=1

            [Skyrim LODGen]
            LODGen1=sky\,None,None,None,None,None,Unchanged,0,Z:\DynDOLOD\Rules\DynDOLOD_SSE_all.ini
            LODGen2=tree,Level0,Billboard1,Billboard1,Level0,FarLOD,Unchanged,17,Z:\DynDOLOD\Rules\DynDOLOD_SSE_High.ini
            LODGen3=\,Level0,Level1,Level2,Level0,FarLOD,Unchanged,1,Z:\DynDOLOD\Rules\DynDOLOD_SSE_High.ini
            """);
        return file;
    }

    [Fact]
    public void ADynDolodPresetSetsTheSettingsThatMeanTheSame()
    {
        var s = new DesktopSettings { OutputFolder = "/b/lod", Worldspaces = "Tamriel" };
        Option(s, "candles", true);

        var result = DynDolodImport.Apply(s, DynDolodPreset());

        Assert.Equal("DynDOLOD", result.Kind);
        Assert.Equal("Medium", Option<string>(s, "preset"));
        Assert.False(Option<bool>(s, "lod32"));
        Assert.True(Option<bool>(s, "tree3d")); Assert.True(Option<bool>(s, "trees"));
        Assert.True(Option<bool>(s, "dynamic")); Assert.True(Option<bool>(s, "grid"));
        Assert.True(Option<bool>(s, "occlusion")); Assert.True(Option<bool>(s, "grass")); Assert.True(Option<bool>(s, "underside"));
        Assert.Equal(.55m, Option<decimal>(s, "grassDensity"));
        Assert.False(Option<bool>(s, "seasons")); Assert.True(Option<bool>(s, "candles")); Assert.True(Option<bool>(s, "fxGlow"));
        Assert.True(Option<bool>(s, "glowWindows")); Assert.False(Option<bool>(s, "parentCopies"));
        Assert.Equal("Tamriel, DLC2SolstheimWorld, Blackreach", s.Worldspaces);
        Assert.Equal("/b/lod", s.OutputFolder);
        Assert.Equal(3, result.MeshRules);
        Assert.Contains("Grass density: 0.08 → 0.55", result.Changes);
        Assert.Contains("Mesh rules preset: High → Medium", result.Changes);
        Assert.Contains("Generate Occlusion.esp: off → on", result.Changes);
        Assert.Contains(result.Changes, c => c.StartsWith("Worldspaces: "));
        Assert.DoesNotContain(result.Changes, c => c.StartsWith("Candle rules"));
        Assert.True(result.Unchanged > 0);
        Assert.Equal(["WindowsHigh", "UpgradeLargeReferences", "ObjectLODMaxTileSize", "OcclusionQuality", "GrassLODMode"], result.NotImported);
        GenerationOptions.Validate(s);
    }

    [Fact]
    public void ATexGenPresetSetsTextureSizesKindsAndLight()
    {
        string file = Path.Combine(_root, "DynDOLOD_SSE_TexGen.ini");
        File.WriteAllText(file, """
            [TexGen]
            OutputPath=Z:\mods\TexGen Output\
            RenderPBRScale=0.6
            StitchObjects=1
            StitchObjectsSize=512
            RenderObjects=1
            RenderObjectsSize=256
            BillboardsUnitsPerPixel=80
            BillboardsMaxSize=1024
            GrassBillboards=0
            GrassHDBillboards=1
            TreeBillboards=1
            TreeBillboardsDirect=100
            TreeBillboardsAmbient=50
            TreeHDBillboards=1
            TreeHDBillboardsDirect=120
            TreeHDBillboardsAmbient=70
            RenderBillboards=0
            RenderBillboardsDirect=40
            RenderBillboardsAmbient=160
            """);
        var s = new DesktopSettings { TextureGenOutput = "/b/textures" };

        var result = DynDolodImport.Apply(s, file);

        Assert.Equal("TexGen", result.Kind);
        Assert.True(s.TextureGenStitched); Assert.Equal(512m, s.TextureGenBaseSize); Assert.Equal(1024m, s.TextureGenMaxSize);
        Assert.True(s.TextureGenTrees); Assert.True(s.TextureGenGrass); Assert.True(s.TextureGenHdViews); Assert.False(s.TextureGenTrunks);
        Assert.Equal(.7m, s.TextureGenTreeAmbient); Assert.Equal(1.2m, s.TextureGenTreeDirect);
        Assert.Equal(1.6m, s.TextureGenTrunkAmbient); Assert.Equal(.4m, s.TextureGenTrunkDirect);
        Assert.Equal(.6m, s.BakePbrScale);
        Assert.Equal(6m, s.TextureGenUnitsPerPixel); Assert.False(s.TextureGenObjects);
        Assert.Equal("/b/textures", s.TextureGenOutput);
        Assert.Contains("Stitch base size: 256 → 512", result.Changes);
        Assert.Equal(["RenderObjects", "RenderObjectsSize", "BillboardsUnitsPerPixel"], result.NotImported);
        Assert.Equal(0, result.MeshRules);
    }

    [Fact]
    public void AFileThatIsNotADynDolodPresetIsRefusedAndChangesNothing()
    {
        // DynDOLOD's own settings file has the same section name and none of a preset's choices.
        string file = Path.Combine(_root, "DynDOLOD_SSE.ini");
        File.WriteAllText(file, "[DynDOLOD]\nWizard=1\nExpert=0\nTreeFullFallBack=1\nOcclusionRadius=100\n");
        var s = new DesktopSettings { Worldspaces = "Tamriel" };
        var error = Assert.Throws<InvalidDataException>(() => DynDolodImport.Apply(s, file));
        Assert.Contains("Presets", error.Message);
        Assert.Equal("Tamriel", s.Worldspaces); Assert.Empty(s.Options);
    }

    [Fact]
    public void ADynDolodPresetWorksAsACustomRuleFile()
    {
        var rules = PenguLOD.Core.Lod.RuleEntry.ParseFile(File.ReadAllText(DynDolodPreset()), "preset", true);
        Assert.Equal(["sky\\", "tree", "\\"], rules.Select(r => r.Mask));
        Assert.Equal("Billboard1", rules[1].Lod8);
    }

    private static void Click(MainWindow w, string text)
    {
        w.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        w.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == text).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs(); w.UpdateLayout();
    }
    private static string Status(MainWindow w) =>
        w.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text?.StartsWith("Detail levels match") == true).Text!;

    [AvaloniaFact]
    public void OverviewAppliesBuiltInPresetsAndSavesLoadsAndDeletesNamedOnes()
    {
        var window = Window(); window.Show();
        try
        {
            window.Settings.OutputFolder = "/b/lod";
            Click(window, "Overview");
            Assert.Equal("Detail levels match High.", Status(window));

            Click(window, "Performance");
            Assert.Equal("Low", Option<string>(window.Settings, "preset"));
            Assert.Equal(128m, window.Settings.TextureGenBaseSize);
            Assert.Equal("Detail levels match Performance.", Status(window));

            Option(window.Settings, "seasons", true); Dispatcher.UIThread.RunJobs();
            var name = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PresetName");
            name.Text = "Low end"; Click(window, "Save current settings");
            string file = Path.Combine(_root, "presets", "Low end.json");
            Assert.True(File.Exists(file));
            var saved = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "SavedPresets");
            Assert.Equal("Low end", saved.SelectedItem);

            Click(window, "Balanced"); Option(window.Settings, "seasons", false); Dispatcher.UIThread.RunJobs();
            Assert.Equal("Detail levels match Balanced.", Status(window));
            Click(window, "Load preset");
            Assert.Equal("Low", Option<string>(window.Settings, "preset")); Assert.True(Option<bool>(window.Settings, "seasons"));
            Assert.Equal("/b/lod", window.Settings.OutputFolder);

            // The Object LOD page shows the loaded values and has the same three buttons.
            Click(window, "Object LOD");
            foreach (var group in window.GetVisualDescendants().OfType<Expander>().ToArray()) group.IsExpanded = true;
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.True(window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content as string == "Seasonal LOD").IsChecked);
            Click(window, "High");
            Assert.Equal("High", Option<string>(window.Settings, "preset")); Assert.Equal(256m, window.Settings.TextureGenBaseSize);
            Assert.True(Option<bool>(window.Settings, "seasons"));

            Click(window, "Overview");
            saved = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "SavedPresets");
            saved.SelectedItem = "Low end"; Click(window, "Delete preset");
            Assert.False(File.Exists(file)); Assert.Empty(saved.ItemsSource!.Cast<string>());
            Click(window, "Load preset");
            Assert.Equal("High", Option<string>(window.Settings, "preset"));    // nothing chosen: a notice, no change

            var result = window.ImportDynDolodSettings(DynDolodPreset());
            Assert.Equal(3, result.MeshRules);
            Assert.Equal("Medium", Option<string>(window.Settings, "preset"));
            Assert.Equal("Tamriel, DLC2SolstheimWorld, Blackreach", window.Settings.Worldspaces);
            Assert.Equal("", window.Settings.CustomRulesFile);
        }
        finally { window.Close(); }
        var reloaded = new SettingsStore(Path.Combine(_root, "settings.json")).Load();
        Assert.Equal("Medium", Option<string>(reloaded, "preset"));
        Assert.Equal(.55m, Option<decimal>(reloaded, "grassDensity"));
    }
}
