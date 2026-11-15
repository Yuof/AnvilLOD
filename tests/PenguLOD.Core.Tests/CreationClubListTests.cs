using PenguLOD.Plugins;
using PenguLOD.Testing;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace PenguLOD.Core.Tests;

/// <summary>Skyrim.ccc lists the Creation Club plugins Skyrim SE loads by itself. Skyrim VR has no such file.</summary>
public sealed class CreationClubListTests
{
    private const string Plugin = "ccTest001-Fixture.esl";

    [Theory]
    [InlineData(GameRelease.SkyrimSE)]
    [InlineData(GameRelease.SkyrimVR)]
    public void AGameWithoutACreationClubListLoadsWithoutAWarning(GameRelease release)
    {
        using var f = new TextureFixture();
        using var game = GameContext.Open(f.Game with { Release = release });
        Assert.DoesNotContain(game.Warnings, w => w.Contains("Skyrim.ccc"));
        Assert.Empty(game.MissingPlugins);
        Assert.Contains(game.LoadOrder.ListedOrder, l => l.ModKey.FileName.String == "Skyrim.esm" && l.Mod is not null);
    }

    [Theory]
    [InlineData(GameRelease.SkyrimSE, true)]
    [InlineData(GameRelease.SkyrimVR, false)]
    public void TheCreationClubListAddsItsPluginsOnlyWhereTheGameReadsIt(GameRelease release, bool loaded)
    {
        using var f = new TextureFixture();
        new SkyrimMod(ModKey.FromNameAndExtension(Plugin), SkyrimRelease.SkyrimSE).WriteToBinary(Path.Combine(f.Data, Plugin));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(f.Data)!, "Skyrim.ccc"), Plugin + "\r\n");
        using var game = GameContext.Open(f.Game with { Release = release });
        Assert.Equal(loaded, game.LoadOrder.ListedOrder.Any(l => l.ModKey.FileName.String == Plugin));
        Assert.DoesNotContain(game.Warnings, w => w.Contains("Skyrim.ccc"));
    }
}
