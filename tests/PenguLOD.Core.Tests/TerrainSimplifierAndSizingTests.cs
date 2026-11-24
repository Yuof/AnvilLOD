using System.Numerics;
using PenguLOD.Core.World;
using PenguLOD.Meshes;
using PenguLOD.Meshes.Nif;
using PenguLOD.Plugins;
using PenguLOD.Testing;
using PenguLOD.Textures;
using PenguLOD.Textures.Bc;
using PenguLOD.Textures.Dds;

namespace PenguLOD.Core.Tests;

public sealed class TerrainSimplifierAndSizingTests
{
    [Fact]
    public void AdaptiveTerrainMesh_IsCrackFree_PinsLod4Borders_AndCollapsesFlatAndUnderwaterTerrain()
    {
        // 1) Flat underwater seafloor at LOD8 collapses from 33,792 triangles down to minimal quads + skirt.
        var flatSea = TerrainTileBuilder.Build(
            new QuadKey("Tamriel", LodLevel.Lod8, 0, 0),
            (_, _) => -2048f,
            (_, _) => 0f,
            skirtDepth: 512f,
            maxError: 16f,
            protectBorders: true,
            optimizeUnseen: true);
        Assert.Equal(64, flatSea.WaterCells);
        // Greedy 2D water merging collapses 64 contiguous same-height cells into 1 quad (2 triangles)!
        var seaNif = NifGeometryReader.Read("sea.btr", flatSea.Bytes, passthru: true, includeUnshaded: true);
        var waterPart = Assert.Single(seaNif.Parts, p => ReferenceEquals(p.Material, NifGeometryReader.UnshadedMaterial));
        Assert.Equal(2, waterPart.TriangleCount);
        var landPart = Assert.Single(seaNif.Parts, p => p.Name == "land");
        Assert.True(landPart.TriangleCount < 64, $"Expected flat seafloor < 64 tris, got {landPart.TriangleCount}");

        // 2) Mountain ridge at LOD4 preserves all 512 outer perimeter vertices when protectBorders=true and has zero interior T-junctions/cracks.
        float? RidgeHeight(float wx, float wy) =>
            800f * MathF.Exp(-MathF.Pow((wx - 8192f) / 2200f, 2f) - MathF.Pow((wy - 8192f) / 3000f, 2f))
            + 120f * MathF.Sin(wx / 900f) * MathF.Cos(wy / 1100f);

        var (positions, indices) = TerrainMeshSimplifier.BuildAdaptive(
            new QuadKey("Tamriel", LodLevel.Lod4, 0, 0),
            RidgeHeight,
            worldMaxError: 6f,
            protectBorders: true,
            optimizeUnseen: false);

        Assert.InRange(indices.Length / 3, 100, 12000); // far smaller than 32,768 unsimplified surface triangles

        // Verify manifold crack-freedom: every directed interior edge (u -> v) must have an exact twin (v -> u),
        // and only perimeter edges may have count == 1.
        var directed = new HashSet<(int, int)>();
        for (int t = 0; t < indices.Length; t += 3)
        {
            int a = indices[t], b = indices[t + 1], c = indices[t + 2];
            Assert.True(directed.Add((a, b)));
            Assert.True(directed.Add((b, c)));
            Assert.True(directed.Add((c, a)));
        }
        int boundaryEdges = 0;
        foreach (var (u, v) in directed)
        {
            if (directed.Contains((v, u))) continue;
            boundaryEdges++;
            var pu = positions[u]; var pv = positions[v];
            bool onOuterEdge = (MathF.Abs(pu.X) < 0.05f && MathF.Abs(pv.X) < 0.05f)
                || (MathF.Abs(pu.X - 4096f) < 0.05f && MathF.Abs(pv.X - 4096f) < 0.05f)
                || (MathF.Abs(pu.Y) < 0.05f && MathF.Abs(pv.Y) < 0.05f)
                || (MathF.Abs(pu.Y - 4096f) < 0.05f && MathF.Abs(pv.Y - 4096f) < 0.05f);
            Assert.True(onOuterEdge, $"Interior crack edge found between {pu} and {pv}");
        }
        Assert.Equal(512, boundaryEdges); // All 4 * 128 pinned LOD4 perimeter segments intact!
    }

    [Fact]
    public void Bc1AndBc5Encoders_RoundTripThroughDdsDecoder_AndPerLevelTerrainSizingWorks()
    {
        var rgba = new byte[16 * 16 * 4];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                int o = (y * 16 + x) * 4;
                rgba[o] = (byte)(40 + x * 10);
                rgba[o + 1] = (byte)(80 + y * 8);
                rgba[o + 2] = 150;
                rgba[o + 3] = 255;
            }

        byte[] bc1Dds = TextureImages.EncodeBc1(new TextureImage(rgba, 16, 16));
        var bc1Info = DdsFile.ReadInfo(bc1Dds);
        Assert.Equal(BcFormat.Bc1, bc1Info.Format);
        var decodedBc1 = DdsFile.DecodeTopMip(bc1Dds);
        double mseBc1 = 0;
        for (int i = 0; i < rgba.Length; i += 4)
            for (int c = 0; c < 3; c++)
            {
                int d = rgba[i + c] - decodedBc1.Rgba[i + c];
                mseBc1 += d * d;
            }
        mseBc1 /= (16 * 16 * 3);
        Assert.True(mseBc1 < 35.0, $"BC1 MSE too high: {mseBc1}");

        byte[] bc5Raw = Bc5Encoder.EncodeImage(rgba, 16, 16);
        using var ms = new MemoryStream();
        DdsFile.WriteBc5(ms, 16, 16, [bc5Raw]);
        var decodedBc5 = DdsFile.DecodeTopMip(ms.ToArray());
        for (int i = 0; i < rgba.Length; i += 4)
        {
            Assert.InRange(Math.Abs(rgba[i] - decodedBc5.Rgba[i]), 0, 3);
            Assert.InRange(Math.Abs(rgba[i + 1] - decodedBc5.Rgba[i + 1]), 0, 3);
        }

        // Verify per-level BC1/BC7 format and distinct diffuse vs normal sizes in TerrainGeneration.
        using var fixture = new TerrainFixture();
        var req = fixture.Request with
        {
            DiffuseSizes = new(128, 64, 64, 64),
            NormalSizes = new(64, 64, 64, 64),
            DiffuseFormats = new("BC1", "BC1", "BC1", "BC1"),
            MeshMaxErrors = new(6f, 16f, 36f, 72f),
        };
        var res = TerrainGeneration.Run(req);
        byte[] diffBytes = File.ReadAllBytes(GamePath.ToFileSystemPath(fixture.Output, @"textures\terrain\FixtureWorld\FixtureWorld.4.-4.-4.dds"));
        byte[] normBytes = File.ReadAllBytes(GamePath.ToFileSystemPath(fixture.Output, @"textures\terrain\FixtureWorld\FixtureWorld.4.-4.-4_n.dds"));
        var diffInfo = DdsFile.ReadInfo(diffBytes);
        var normInfo = DdsFile.ReadInfo(normBytes);
        Assert.Equal(BcFormat.Bc1, diffInfo.Format);
        Assert.Equal(128, diffInfo.Width);
        Assert.Equal(BcFormat.Bc7, normInfo.Format);
        Assert.Equal(64, normInfo.Width);
        Assert.True(res.Tiles[0].Triangles < 2000, $"Expected simplified terrain tile < 2000 tris, got {res.Tiles[0].Triangles}");
    }
}
