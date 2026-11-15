using System.Numerics;
using PenguLOD.Meshes;
using PenguLOD.Plugins.Authoring;
using Xunit;

namespace PenguLOD.Core.Tests;

/// <summary>Trees and trunks are lit the way TexGen lights them: colour x (ambient + direct x the share of the surface that faces up).</summary>
public class BillboardLightTests
{
    private static LodMesh Card(Vector3 normal) => new()
    {
        Path = "card", Parts = [new() { Material = new([@"textures\bark.dds", "", "", "", "", ""], 0, 0, 3, true, 4844, 128, Vector3.Zero, 1),
            Positions = [new(0,0,0), new(100,0,0), new(0,0,100), new(100,0,100)],
            UVs = [new(0,1), new(1,1), new(0,0), new(1,0)],
            Normals = Enumerable.Repeat(Vector3.Normalize(normal),4).ToArray(), Tangents = Enumerable.Repeat(-Vector3.UnitZ,4).ToArray(),
            Bitangents = Enumerable.Repeat(Vector3.UnitX,4).ToArray(), Triangles = [0,1,2,1,3,2] }]
    };
    private static (byte[],int,int)? Grey(string path) => (new byte[] { 100, 100, 100, 255 }, 1, 1);
    private static int Centre(Vector3 normal, BillboardLight? light = null, float brightness = 1)
        => BillboardRenderer.Render(Card(normal), Grey, 32, light: light, brightness: brightness)!.Rgba[(16 * 32 + 16) * 4];

    [Fact]
    public void A_surface_that_faces_sideways_gets_the_ambient_part_alone()
    {
        Assert.InRange(Centre(-Vector3.UnitY), 59, 61);                          // trees are the default: 60%
        Assert.InRange(Centre(-Vector3.UnitY, BillboardLight.Tree), 59, 61);
        Assert.InRange(Centre(-Vector3.UnitY, BillboardLight.Trunk), 149, 151);  // 150%: bark faces sideways and must not come out dark
        Assert.InRange(Centre(-Vector3.UnitY, BillboardLight.Trunk, brightness: .5f), 74, 76);
    }

    [Fact]
    public void The_light_comes_from_above_and_never_from_below()
    {
        Assert.InRange(Centre(new(0, -.6f, .8f)), 167, 169);                     // 100 x (0.6 + 1.35 x 0.8)
        Assert.InRange(Centre(new(0, -.6f, -.8f)), 59, 61);                      // an underside is not lit and not darkened
        Assert.InRange(Centre(new(0, -.6f, .8f), new(.5f, 0)), 49, 51);          // no direct light: the same everywhere
        Assert.Equal(255, Centre(new(0, -.6f, .8f), new(2, 2)));                 // bright surfaces stop at white
    }

    [Fact]
    public void Grass_is_lit_from_the_front_and_above_on_both_sides()
    {
        // 100 x (0.7 + 0.3 x |normal . light|), the light towards (0.3, -1, 0.8): 0.76 of it reaches a card that faces the viewer.
        Assert.InRange(Centre(-Vector3.UnitY, BillboardLight.Grass), 92, 94);
        Assert.InRange(Centre(Vector3.UnitY, BillboardLight.Grass), 92, 94);     // the back of a blade is lit the same
        Assert.InRange(Centre(new(0, -.6f, .8f), BillboardLight.Grass), 98, 100);
    }

    [Fact]
    public void The_defaults_are_TexGens_and_bad_values_are_refused()
    {
        Assert.Equal(new BillboardLight(.6f, 1.35f), new BillboardSettings().Trees);
        Assert.Equal(new BillboardLight(.7f, .3f, Frontal: true), new BillboardSettings().Grass);
        Assert.Equal(new BillboardLight(1.5f, .5f), new BillboardSettings().Trunks);
        Assert.Equal(new BillboardLight(1, 0), new BillboardSettings(TreeLight: new(1, 0)).Trees);
        Assert.Throws<ArgumentException>(() => new BillboardSettings(TrunkLight: new(0, 0)).Validate());
        Assert.Throws<ArgumentException>(() => new BillboardSettings(GrassLight: new(-1, 1)).Validate());
        Assert.Throws<ArgumentException>(() => new BillboardSettings(TreeLight: new(1, 5)).Validate());
    }
}
