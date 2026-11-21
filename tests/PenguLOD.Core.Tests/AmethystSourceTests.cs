using System.Text.Json;
using PenguLOD.Plugins.Mo2;
using Xunit;

namespace PenguLOD.Core.Tests;

/// <summary>An Amethyst Mod Manager staging folder read like an MO2 instance, without Amethyst deploying it.</summary>
public sealed class AmethystSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pengulod-amethyst-" + Guid.NewGuid().ToString("N"));
    private string Staging => Path.Combine(_root, "Games", "Amethyst", "Skyrim Special Edition");
    private string Game => Path.Combine(_root, "Game");
    private string Config => Path.Combine(_root, "config", "AmethystModManager");

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private void Put(string path, string text = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private void Build()
    {
        // The game after an Amethyst deploy: its own files moved to Data_Core, Data rebuilt from links (here a stale file).
        Put(Path.Combine(Game, "Data_Core", "Skyrim.esm"));
        Put(Path.Combine(Game, "Data_Core", "meshes", "core.nif"));
        Put(Path.Combine(Game, "Data", "meshes", "stale.nif"));
        Put(Path.Combine(Staging, "filegraph.sqlite3"));
        Directory.CreateDirectory(Path.Combine(Staging, "Root_Folder"));
        Put(Path.Combine(Staging, "mods", "Plain", "meshes", "a.nif"));
        Put(Path.Combine(Staging, "mods", "Wrapped In Data", "Data", "meshes", "b.nif"));
        Put(Path.Combine(Staging, "mods", "Wrapped In Data", "Data", "Wrapped.esp"));
        Put(Path.Combine(Staging, "mods", "Promoted", "Option A", "meshes", "c.nif"));
        Put(Path.Combine(Staging, "mods", "Promoted", "Option A", "meshes", "disabled.nif"));
        Put(Path.Combine(Staging, "mods", "Promoted", "Option A", "meshes", "toroot.nif"));
        Put(Path.Combine(Staging, "mods", "Promoted", "Option A", "textures", "c.dds"));
        Put(Path.Combine(Staging, "mods", "Locked", "meshes", "locked.nif"));
        Put(Path.Combine(Staging, "mods", "Elsewhere", "meshes", "elsewhere.nif"));
        Put(Path.Combine(Staging, "overwrite", "meshes", "a.nif"), "overwrite");
        string profile = Path.Combine(Staging, "profiles", "test");
        // Highest priority first. "*" is an enabled mod Amethyst locks on; the separator deploys the mods after it elsewhere.
        Put(Path.Combine(profile, "modlist.txt"), "-Routed_separator\n+Elsewhere\n-Main_separator\n*Locked\n+Promoted\n+Wrapped In Data\n+Plain\n-Off\n");
        Put(Path.Combine(profile, "plugins.txt"), "*Wrapped.esp\n");
        Put(Path.Combine(profile, "loadorder.txt"), "Skyrim.esm\nWrapped.esp\n");
        Put(Path.Combine(profile, "profile_state.json"), """
            {
              "mod_strip_prefixes": {"Promoted": ["Option A"]},
              "excluded_mod_files": {"Promoted": ["option a/meshes/disabled.nif"]},
              "root_mod_files": {"Promoted": ["option a/meshes/toroot.nif"]},
              "separator_deploy_paths": {"Routed_separator": {"path": "/somewhere/else", "raw": false}}
            }
            """);
        Put(Path.Combine(Staging, "profiles", "default", "modlist.txt"), "");
        // Serialized, not interpolated: a Windows path has backslashes, which JSON needs escaped.
        Put(Path.Combine(Config, "games", "Skyrim Special Edition", "paths.json"), JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["game_path"] = Game, ["prefix_path"] = Path.Combine(_root, "pfx"), ["staging_path"] = Staging, ["deploy_mode"] = "symlink",
        }));
        Put(Path.Combine(Config, "amethyst.ini"), "[session]\nlast_game = Skyrim Special Edition\nlast_profile = test\n");
    }

    [Fact]
    public void AStagingFolderIsReadWithAmethystsRulesWithoutADeploy()
    {
        Build();
        Assert.True(AmethystStaging.IsStaging(Staging));
        var instance = Mo2Instance.OpenAmethyst(Staging, null, [Config]);
        Assert.NotNull(instance.Amethyst);
        Assert.Equal(Game, instance.GamePath);
        Assert.Equal(Path.Combine(_root, "pfx"), instance.WinePrefix);
        Assert.Equal(Path.Combine(Game, "Data_Core"), instance.GameDataFolder);   // the deployed Data is not read
        Assert.Equal("test", instance.SelectedProfile);                            // the profile Amethyst has open
        Assert.Equal(["default", "test"], instance.ListProfiles());

        var profile = instance.OpenProfile();
        Assert.Equal(["Plain", "Wrapped In Data", "Promoted", "Locked"], profile.EnabledModsLowToHigh);
        Assert.Equal(["Elsewhere"], profile.RoutedElsewhere);
        Assert.Equal(["Skyrim.esm", "Wrapped.esp"], profile.EnabledPlugins);

        var data = VirtualDataIndex.Build(profile, ["meshes"]);
        Assert.Equal(Path.Combine(Game, "Data_Core", "meshes", "core.nif"), data.ResolveLoose(@"meshes\core.nif"));
        Assert.Null(data.ResolveLoose(@"meshes\stale.nif"));
        Assert.Equal(Path.Combine(Staging, "overwrite", "meshes", "a.nif"), data.ResolveLoose(@"meshes\a.nif"));   // Overwrite wins
        Assert.Equal(Path.Combine(Staging, "mods", "Wrapped In Data", "Data", "meshes", "b.nif"), data.ResolveLoose(@"meshes\b.nif"));
        Assert.Equal(Path.Combine(Staging, "mods", "Wrapped In Data", "Data", "Wrapped.esp"), data.ResolveRootFile("Wrapped.esp"));
        Assert.Equal(Path.Combine(Staging, "mods", "Promoted", "Option A", "meshes", "c.nif"), data.ResolveLoose(@"meshes\c.nif"));
        Assert.Null(data.ResolveLoose(@"meshes\disabled.nif"));
        Assert.Null(data.ResolveLoose(@"meshes\toroot.nif"));
        Assert.NotNull(data.ResolveLoose(@"meshes\locked.nif"));
        Assert.Null(data.ResolveLoose(@"meshes\elsewhere.nif"));
        // Outside the indexed folders, files are looked up through the same rules.
        Assert.Equal(Path.Combine(Staging, "mods", "Promoted", "Option A", "textures", "c.dds"), data.FindLoose(@"textures\c.dds"));
        Assert.Null(data.FindLoose(@"option a\textures\c.dds"));
    }

    [Fact]
    public void AProfileWithItsOwnModsUsesThemAndTypedLocationsWin()
    {
        Build();
        string own = Path.Combine(Staging, "profiles", "own");
        Put(Path.Combine(own, "modlist.txt"), "+Mine\n");
        Put(Path.Combine(own, "mods", "Mine", "meshes", "mine.nif"));
        Put(Path.Combine(own, "profile_state.json"), """{"profile_settings": {"profile_specific_mods": true}}""");
        var profile = Mo2Instance.OpenAmethyst(Staging, null, [Config]).OpenProfile("own");
        Assert.Equal(Path.Combine(own, "mods"), profile.ModsFolder);
        Assert.Equal(Path.Combine(own, "overwrite"), profile.OverwriteFolder);
        Assert.NotNull(VirtualDataIndex.Build(profile, ["meshes"]).ResolveLoose(@"meshes\mine.nif"));

        // Without Amethyst's settings the game folder has to be typed; typed folders win over the settings.
        Assert.Throws<InvalidDataException>(() => Mo2Instance.OpenAmethyst(Staging, null, [Path.Combine(_root, "nothing")]));
        // A settings file that is not valid JSON is named, instead of being reported as a missing game folder.
        string broken = Path.Combine(_root, "broken");
        Put(Path.Combine(broken, "games", "Skyrim Special Edition", "paths.json"), "{\"game_path\": \"C:\\Games\\Skyrim\"");
        var unreadable = Assert.Throws<InvalidDataException>(() => Mo2Instance.OpenAmethyst(Staging, null, [broken]));
        Assert.Contains("paths.json", unreadable.Message);
        Assert.Contains("not valid JSON", unreadable.Message);
        string other = Path.Combine(_root, "Other Game");
        Put(Path.Combine(other, "Data", "Skyrim.esm"));
        Assert.Equal(other, Mo2Instance.OpenAmethyst(Staging, new Mo2Locations(GamePath: other), [Config]).GamePath);
    }
}
