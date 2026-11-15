using Mutagen.Bethesda;
using PenguLOD.Core.World;
using PenguLOD.Meshes;
using PenguLOD.Plugins;
using PenguLOD.Testing;
using Xunit;

namespace PenguLOD.Core.Tests;

/// <summary>
/// An installed PenguLOD output is part of the load order of the next run. Its stand-ins for missing textures must not pass for the
/// real textures, or every second output has none and the textures are missing in game.
/// </summary>
public sealed class StandInTextureTests
{
    [Fact]
    public void A_stand_in_from_an_earlier_output_is_told_from_a_real_texture()
    {
        using var f = new TextureFixture();
        // What an installed earlier output holds: stand-ins for a diffuse and a normal map, under the old mixed-case spelling too.
        StubTextures.Write(f.Data, @"textures\absent\wall.dds");
        StubTextures.Write(f.Data, @"textures\absent\wall_n.dds");
        TextureFixture.Put(f.Data, @"textures\Absent\Legacy\Creek_Blue.dds", StubTextures.Build(70, 98, 110));
        // A real texture of the same size, and one that is a stand-in's bytes for another name (another colour).
        TextureFixture.Put(f.Data, @"textures\real\flat.dds", StubTextures.Build(1, 2, 3));
        TextureFixture.Put(f.Data, @"textures\real\snow.dds", StubTextures.Build(110, 105, 95));
        var assets = AssetIndex.Build(GameRelease.SkyrimSE, f.Data);

        Assert.True(StubTextures.IsStandIn(assets, @"textures\absent\wall.dds"));
        Assert.True(StubTextures.IsStandIn(assets, @"Textures\Absent\Wall_N.dds"));
        Assert.True(StubTextures.IsStandIn(assets, @"textures\absent\legacy\creek_blue.dds"));
        Assert.False(StubTextures.IsStandIn(assets, @"textures\real\flat.dds"));
        Assert.False(StubTextures.IsStandIn(assets, @"textures\real\snow.dds"));
        Assert.False(StubTextures.IsStandIn(assets, @"textures\red.dds"));          // an ordinary texture
        Assert.False(StubTextures.IsStandIn(assets, @"textures\nowhere.dds"));      // nothing there
        Assert.False(StubTextures.IsStandIn(assets, @"d:\not\a\game\path.dds"));
    }

    [Fact]
    public void The_audit_reports_a_texture_that_only_exists_as_a_stand_in()
    {
        using var f = new TextureFixture();
        StubTextures.Write(f.Data, @"textures\absent\wall.dds");
        var assets = AssetIndex.Build(GameRelease.SkyrimSE, f.Data);
        var mesh = new LodMesh { Path = "wall_lod.nif", Parts = [TextureFixture.Card("wall", @"textures\absent\wall.dds"), TextureFixture.Card("red", @"textures\red.dds")] };

        // As before the fix: the stand-in passes for the texture and nothing is reported.
        Assert.Empty(LodGenerator.FindMissingTextures([mesh], assets.Exists));
        var missing = LodGenerator.FindMissingTextures([mesh], tex => assets.Exists(tex) && !StubTextures.IsStandIn(assets, tex));
        Assert.Equal([@"textures\absent\wall.dds"], missing.Keys);
    }
}
