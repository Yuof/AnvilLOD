using PenguLOD.Core.Lod;
using PenguLOD.Core.World;
using PenguLOD.Plugins;
using PenguLOD.Testing;

namespace PenguLOD.Core.Tests;

[Collection("CLI")]
public class SeasonOrderTests
{
    [Fact]
    public void MainFormSwapLoadsFirstThenCaseSensitiveNameOrder()
    {
        string[] files = ["happylittletrees_WIN.ini", "MainFormSwap_WIN.ini", "Zed_WIN.ini", "Apple_WIN.ini", "mainformswap_SUM.ini"];
        Assert.Equal(["MainFormSwap_WIN.ini", "mainformswap_SUM.ini", "Apple_WIN.ini", "Zed_WIN.ini", "happylittletrees_WIN.ini"],
            SeasonSwaps.LoadOrder(files, f => f));
    }

    [Fact]
    public void ModInisSortingBeforeMainFormSwapStillOverrideIt()
    {
        // "Fixture_WIN.ini" sorts before "MainFormSwap_WIN.ini". The game applies MainFormSwap first, so the mod's swap wins.
        using var f = new TerrainFixture(seasons: true);
        TextureFixture.Put(f.Textures.Data, @"seasons\\MainFormSwap_WIN.ini", System.Text.Encoding.UTF8.GetBytes("[LandTextures]\nFixtureLand|FixtureLand\n"));
        using var game = GameContext.Open(f.Game);
        var assets = game.BuildAssets();
        Assert.Equal(["MainFormSwap_WIN.ini", "Fixture_WIN.ini"], SeasonalAssets.SeasonFiles(assets).Select(assets.FileNameOf));
        var winter = SeasonalAssets.Read(game, assets, "LandTextures")["WIN"];
        var swap = Assert.Single(winter);
        Assert.NotEqual(swap.Key, swap.Value);
    }

    [Fact]
    public void ASeasonWithoutResolvedSwapsIsLeftOutAndReported()
    {
        using var f = new TerrainFixture(seasons: true);
        TextureFixture.Put(f.Textures.Data, @"seasons\\Optional_SUM.ini", System.Text.Encoding.UTF8.GetBytes("[LandTextures]\nFixtureLand|0x800~NotInstalled.esp\n"));
        TextureFixture.Put(f.Textures.Data, @"seasons\\Cancel_AUT.ini", System.Text.Encoding.UTF8.GetBytes("[LandTextures]\nFixtureLand|FixtureLand\n"));
        using var game = GameContext.Open(f.Game);
        var assets = game.BuildAssets();
        var warnings = new List<string>();
        var seasons = SeasonalAssets.Read(game, assets, "LandTextures", warn: warnings.Add);
        Assert.Equal(["WIN"], seasons.Keys.Order());
        Assert.Contains(warnings, w => w.Contains("Optional_SUM.ini") && w.Contains("NotInstalled.esp"));
        Assert.Throws<InvalidDataException>(() => SeasonalAssets.Read(game, assets, "LandTextures", strict: true));
    }

    [Theory]
    [InlineData(null, "AUT,SPR,SUM,WIN", null)]
    [InlineData("  ", "AUT,SPR,SUM,WIN", null)]
    [InlineData("winter", "WIN", "WIN")]
    [InlineData("SUM; win", "SUM,WIN", "WIN,SUM")]
    [InlineData("Fall Spring", "AUT,SPR", "SPR,AUT")]
    [InlineData("aut,spr,sum,win", "AUT,SPR,SUM,WIN", null)]
    public void ASeasonSelectionAcceptsNamesAndSuffixesAndAllFourIsNoSelection(string? only, string selected, string? stored)
    {
        Assert.Equal(selected, string.Join(',', SeasonSwaps.Selection(only).Order(StringComparer.Ordinal)));
        Assert.Equal(stored, SeasonSwaps.SelectionText(only));
    }

    [Theory]
    [InlineData("monsoon")] [InlineData("WIN,xyz")] [InlineData(",;")]
    public void AnUnknownOrEmptySeasonSelectionIsRejected(string only) =>
        Assert.Throws<ArgumentException>(() => SeasonSwaps.Selection(only));

    [Fact]
    public void ASeasonLeftOutIsNotReadSoItsBrokenInisNeitherWarnNorStopAStrictRun()
    {
        using var f = new TerrainFixture(seasons: true);
        TextureFixture.Put(f.Textures.Data, @"seasons\\Broken_SUM.ini", System.Text.Encoding.UTF8.GetBytes("[LandTextures]\nFixtureLand|0x800~NotInstalled.esp\n"));
        using var game = GameContext.Open(f.Game);
        var assets = game.BuildAssets();
        var warnings = new List<string>();
        var winter = SeasonalAssets.Read(game, assets, "LandTextures", strict: true, warn: warnings.Add, only: SeasonSwaps.Selection("winter"));
        Assert.Equal(["WIN"], winter.Keys);
        Assert.Empty(warnings);
        Assert.Empty(SeasonalAssets.Read(game, assets, "LandTextures", only: SeasonSwaps.Selection("spring")));
        Assert.Throws<InvalidDataException>(() => SeasonalAssets.Read(game, assets, "LandTextures", strict: true, only: SeasonSwaps.Selection("summer")));
    }

    [Fact]
    public void OnlySeasonsNeedsSeasonsAndKnownNamesOnTheCommandLine()
    {
        Assert.Equal(2, PenguLOD.Cli.Program.Main(["scan", "--only-seasons", "winter"]));
        Assert.Equal(2, PenguLOD.Cli.Program.Main(["scan", "--seasons", "--only-seasons", "monsoon"]));
        Assert.Equal("winter", PenguLOD.Cli.CliArgs.Parse(["--seasons", "--only-seasons", "winter"]).Get("only-seasons"));
    }

    [Fact]
    public void StrictSeasonsNeedSeasonsOnTheCommandLine()
    {
        Assert.Equal(2, PenguLOD.Cli.Program.Main(["scan", "--seasons-strict"]));
        Assert.True(PenguLOD.Cli.CliArgs.Parse(["--seasons", "--seasons-strict"]).Has("seasons-strict"));
    }
}
