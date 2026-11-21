using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using PenguLOD.Plugins;
using PenguLOD.Testing;
using Xunit;

namespace PenguLOD.Core.Tests;

public sealed class MissingMasterTests
{
    [Fact]
    public void AnEnabledPluginWhoseMasterIsNotEnabledIsNamedWithThatMaster()
    {
        // A tester disabled JK's Solitude Outskirts and kept a plugin that needs it. Grass caching stopped with FasterNGIO's
        // "missing master in load order: jk's solitude outskirts.esp", which does not say which plugin needs it.
        using var f = new TerrainFixture(editMod: mod =>
        {
            var target = mod.Statics.AddNew(); target.EditorID = "NeedsAnotherPlugin";
            target.Model = new Model { File = @"fixture\object.nif" };
            var alternate = new AlternateTexture { Name = "object:0", Index = 0 };
            alternate.NewTexture.SetTo(new FormKey(ModKey.FromNameAndExtension("JK's Solitude Outskirts.esp"), 0x800));
            target.Model.AlternateTextures = [alternate];
        });
        using var game = GameContext.Open(f.Game);
        var missing = Assert.Single(game.MissingMasters);
        Assert.Equal("JK's Solitude Outskirts.esp", missing.Master);
        Assert.EndsWith(".esm", missing.Plugin);
    }
}
