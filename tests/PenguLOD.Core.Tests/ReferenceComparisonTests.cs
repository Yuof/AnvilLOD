using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using PenguLOD.Meshes.Authoring;
using PenguLOD.Meshes.Nif;
using PenguLOD.Plugins;
using PenguLOD.Testing;
using Xunit;

namespace PenguLOD.Core.Tests;

public sealed class ReferenceComparisonTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pengulod-reference-tests-" + Guid.NewGuid().ToString("N"));
    private string Folder(string name) => Path.Combine(_root, name);
    private void Put(string folder, string path, byte[] bytes)
    {
        string file = Path.Combine(Folder(folder), path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllBytes(file, bytes);
    }
    private static byte[] Block(string root, params string[] textures) => LodNifWriter.Write(root,
        textures.Select((t, i) => TextureFixture.Card("card" + i, t)).ToArray());
    private static byte[] Grey(byte value, int size)
    {
        var pixels = new byte[size * size * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = pixels[i + 1] = pixels[i + 2] = value; pixels[i + 3] = 255; }
        return PenguLOD.Textures.TextureImages.Encode(new(pixels, size, size));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void Shapes_are_read_with_their_layer_flags_and_first_texture()
    {
        var shapes = NifCostReader.ReadShapes(Block("PL:grass:4", @"textures\Red.dds", @"textures\green.dds"));
        Assert.Equal(2, shapes.Count);
        Assert.All(shapes, s => { Assert.Equal("grass", s.Layer); Assert.Equal(2, s.Triangles); Assert.Equal(4, s.Vertices); });
        Assert.Equal([@"textures\green.dds", @"textures\red.dds"], shapes.Select(s => s.Texture).Order(StringComparer.Ordinal).ToArray());
        // The totals are what the cost reader already reported.
        var cost = Assert.Single(NifCostReader.Read(Block("PL:grass:4", @"textures\Red.dds", @"textures\green.dds")));
        Assert.Equal((4L, 8L, 2), (cost.Triangles, cost.Vertices, cost.MeshGroups));
    }

    [Fact]
    public void Blocks_textures_and_plugins_of_two_outputs_are_measured_side_by_side()
    {
        // Output: one LOD4 block with three shapes on three textures; the reference draws the same block with one.
        Put("out", "meshes/terrain/World/objects/World.4.0.0.bto", Block("PL:objects:4", @"textures\a.dds", @"textures\b.dds", @"textures\c.dds"));
        Put("ref", "Meshes/Terrain/World/Objects/World.4.0.0.bto", Block("obj", @"textures\atlas.dds"));
        Put("out", "meshes/terrain/World/objects/World.8.0.0.bto", Block("PL:grass:8", @"textures\a.dds"));
        Put("ref", "meshes/terrain/World/objects/World.4.4.0.bto", Block("obj", @"textures\atlas.dds"));
        Put("out", "meshes/terrain/Elsewhere/objects/Elsewhere.4.0.0.bto", Block("PL:objects:4", @"textures\a.dds"));
        // A billboard under the same path: the output's is half as bright and a quarter of the pixels.
        Put("out", "textures/terrain/lodgen/skyrim.esm/tree_00012345_1.dds", Grey(60, 4));
        Put("ref", "Textures/Terrain/LODGen/Skyrim.esm/Tree_00012345_1.dds", Grey(120, 8));
        Put("ref", "textures/terrain/lodgen/skyrim.esm/other_00012346_1.dds", Grey(120, 8));
        var plugin = new SkyrimMod(ModKey.FromNameAndExtension("PenguLOD.esp"), SkyrimRelease.SkyrimSE);
        plugin.Statics.AddNew(); plugin.Statics.AddNew(); plugin.Trees.AddNew();
        Directory.CreateDirectory(Folder("out")); plugin.WriteToBinary(Path.Combine(Folder("out"), "PenguLOD.esp"));

        var result = ReferenceComparison.Compare([Folder("out")], [Folder("ref")]);

        var lod4 = Assert.Single(result.Blocks, b => b.Level == 4);
        Assert.Equal(("World", "objects", 1, 0, 1), (lod4.World, lod4.Kind, lod4.Common, lod4.OnlyOutput, lod4.OnlyReference));
        Assert.Equal((6L, 2L), (lod4.OutputTriangles, lod4.ReferenceTriangles));
        Assert.Equal((3.0, 1.0, 3.0, 1.0), (lod4.OutputShapesPerBlock, lod4.ReferenceShapesPerBlock, lod4.OutputTexturesPerBlock, lod4.ReferenceTexturesPerBlock));
        var lod8 = Assert.Single(result.Blocks, b => b.Level == 8);
        Assert.Equal((0, 1, 0), (lod8.Common, lod8.OnlyOutput, lod8.OnlyReference));
        Assert.Equal(["Elsewhere"], result.WorldsOnlyInOutput);
        Assert.Empty(result.WorldsOnlyInReference);
        var layer = Assert.Single(result.Layers);
        Assert.Equal(("World", 4, "objects", "objects", 6L, 3L), (layer.World, layer.Level, layer.Kind, layer.Layer, layer.Triangles, layer.Shapes));
        Assert.Equal(3, result.Shaders.Where(s => s.Side == "output").Sum(s => s.Shapes));
        Assert.Equal(1, result.Shaders.Where(s => s.Side == "reference").Sum(s => s.Shapes));
        Assert.All(result.Shaders, s => { Assert.Equal(8, s.Flags1.Length); Assert.Equal(8, s.Flags2.Length); });

        var billboards = Assert.Single(result.Textures, t => t.Group == "billboards");
        Assert.Equal((1, 0, 1, 1), (billboards.Common, billboards.OnlyOutput, billboards.OnlyReference, billboards.Sampled));
        Assert.Equal(0.25, billboards.MedianPixelRatio!.Value, 3);
        Assert.InRange(billboards.MedianBrightnessRatio!.Value, 0.48, 0.52);

        var records = Assert.Single(result.Plugins);
        Assert.Equal(("output", "PenguLOD.esp"), (records.Side, records.File));
        Assert.Equal((2, 1, 1), (records.Records["STAT"], records.Records["TREE"], records.Records["TES4"]));
        Assert.Empty(result.Warnings);
        Assert.Contains("World", result.Summary());

        // One worldspace only, and no texture sample.
        var filtered = ReferenceComparison.Compare([Folder("out")], [Folder("ref")], ["elsewhere"], textureSamples: 0);
        Assert.Empty(filtered.Blocks);
        Assert.Equal(0, Assert.Single(filtered.Textures, t => t.Group == "billboards").Sampled);
        Assert.Throws<DirectoryNotFoundException>(() => ReferenceComparison.Compare([Folder("missing")], [Folder("ref")]));
        Assert.Throws<ArgumentException>(() => ReferenceComparison.Compare([], [Folder("ref")]));
    }

    [Fact]
    public void Brightness_ignores_transparent_texels_and_the_size_of_the_picture()
    {
        static byte[] Picture(int size, byte value)
        {
            // Left half opaque at the given grey, right half transparent white.
            var rgba = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int i = (y * size + x) * 4; bool opaque = x < size / 2;
                    rgba[i] = rgba[i + 1] = rgba[i + 2] = opaque ? value : (byte)255; rgba[i + 3] = opaque ? (byte)255 : (byte)0;
                }
            return rgba;
        }
        var small = ReferenceComparison.Light(Picture(16, 100), 16, 16)!.Value;
        var large = ReferenceComparison.Light(Picture(256, 100), 256, 256)!.Value;
        Assert.Equal(100, small.Mean, 3); Assert.Equal(100, large.Mean, 3);
        Assert.Equal(0, small.Spread, 3);
        Assert.Null(ReferenceComparison.Light(new byte[16 * 16 * 4], 16, 16));    // nothing opaque
    }

    [Fact]
    public void A_file_that_is_not_a_plugin_is_refused()
    {
        Put("out", "broken.esp", [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24]);
        Assert.Throws<InvalidDataException>(() => ReferenceComparison.CountRecords(Path.Combine(Folder("out"), "broken.esp")));
    }
}
