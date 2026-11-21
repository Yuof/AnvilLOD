using PenguLOD.Core.World;
using PenguLOD.Plugins;
using PenguLOD.Testing;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace PenguLOD.Core.Tests;

public sealed class TerrainSelectionTests
{
    [Fact]
    public void ASelectedWorldWithNothingToGenerateIsSkippedAndAWorldThatFailsIsNamed()
    {
        // A tester ticked every world; terrain was discarded after 20 minutes because two of them have no landscape at all.
        using var f = new TerrainFixture(editMod: mod =>
        {
            var empty = mod.Worldspaces.AddNew(); empty.EditorID = "EmptyWorld";
            var unset = mod.Worldspaces.AddNew(); unset.EditorID = "NoLodSettingsWorld";
        });
        TextureFixture.Put(f.Textures.Data, @"lodsettings\EmptyWorld.lod", new LodSettings(-4, -4, 4, 4, 4).ToBytes());

        var result = TerrainGeneration.Run(f.Request with { Worldspaces = ["FixtureWorld", "EmptyWorld"] });
        Assert.Contains(result.Warnings, w => w.StartsWith("EmptyWorld: no landscape", StringComparison.Ordinal));

        var failed = Assert.Throws<InvalidDataException>(() => TerrainGeneration.Run(f.Request with { Worldspaces = ["FixtureWorld", "NoLodSettingsWorld"] }));
        Assert.Contains("NoLodSettingsWorld", failed.Message);
    }
}
