using System.Numerics;
using System.Text;
using PenguLOD.Core.Lod;
using PenguLOD.Core.Occlusion;
using PenguLOD.Core.Textures;
using PenguLOD.Core.World;
using PenguLOD.Meshes;
using PenguLOD.Meshes.Authoring;
using PenguLOD.Plugins;
using PenguLOD.Plugins.Authoring;
using PenguLOD.Testing;
using PenguLOD.Textures;
using PenguLOD.Textures.Bc;
using PenguLOD.Textures.Dds;
using Xunit;

namespace PenguLOD.Core.Tests;

public class Phase3TexturesAndRasterizerTests
{
    [Fact]
    public void MultiMode_Bc7_Handles_TwoSubset_Edges_Rotations_And_Alpha_With_High_Fidelity()
    {
        // Non-collinear two-colour opaque block: top half pure red (240, 10, 10), bottom half pure cyan (10, 230, 240),
        // with one intermediate pixel (240, 230, 10) in top-left so 1-subset PCA cannot pass through all three!
        var block = new byte[64];
        for (int i = 0; i < 16; i++)
        {
            bool top = i < 8;
            block[i * 4] = i == 0 ? (byte)240 : top ? (byte)240 : (byte)10;
            block[i * 4 + 1] = i == 0 ? (byte)230 : top ? (byte)10 : (byte)230;
            block[i * 4 + 2] = i == 0 ? (byte)10 : top ? (byte)10 : (byte)240;
            block[i * 4 + 3] = 255;
        }
        Span<byte> enc = stackalloc byte[16];
        Span<byte> dec = stackalloc byte[64];
        Bc7Encoder.EncodeBlock(block, enc);
        Bc7Decoder.DecodeBlock(enc, dec);
        double mse = 0;
        for (int i = 0; i < 64; i++) { double d = block[i] - dec[i]; mse += d * d; }
        mse /= 64.0;
        Assert.True(mse < 12.0, $"Expected multi-mode BC7 MSE < 12 on 2-subset block, got {mse:F2}");

        // Mode 7 (2-subset RGB + alpha cutout 0 vs 255): sharp colour boundary AND clean cutout silhouette
        for (int i = 0; i < 16; i++) block[i * 4 + 3] = (byte)(i % 2 == 0 ? 0 : 255);
        Bc7Encoder.EncodeBlock(block, enc);
        Bc7Decoder.DecodeBlock(enc, dec);
        for (int i = 0; i < 16; i++)
            Assert.InRange(Math.Abs(block[i * 4 + 3] - dec[i * 4 + 3]), 0, 8);

        // Mode 5 (1-subset RGB + smooth 8-bit/3-bit varying alpha): accurate intermediate alpha
        var smoothAlpha = new byte[64];
        for (int i = 0; i < 16; i++)
        {
            smoothAlpha[i * 4] = 180; smoothAlpha[i * 4 + 1] = 120; smoothAlpha[i * 4 + 2] = 70;
            smoothAlpha[i * 4 + 3] = (byte)(i * 17);
        }
        Bc7Encoder.EncodeBlock(smoothAlpha, enc);
        Bc7Decoder.DecodeBlock(enc, dec);
        for (int i = 0; i < 16; i++)
            Assert.InRange(Math.Abs(smoothAlpha[i * 4 + 3] - dec[i * 4 + 3]), 0, 12);
    }

    [Fact]
    public void TruePbrCatalog_And_PbrLodConverter_Bake_Rmaos_Metallic_Ao_And_Normal_Gloss()
    {
        using var f = new TextureFixture();
        TextureFixture.Put(f.Data, @"PBRJSONs\architecture\stone.json",
            Encoding.UTF8.GetBytes("""[{"match_diffuse":"architecture\\stonewall","base_color_scale":[0.8,0.9,1.0],"roughness_scale":0.5,"roughness_offset":0.1}]"""));
        using var game = GameContext.Open(f.Game);
        var assets = game.BuildAssets(_ => true);
        var catalog = TruePbrCatalog.Load(assets, assets.EnumeratePaths("pbrjsons\\"));
        Assert.Equal(1, catalog.Count);
        var rule = catalog.Match(@"textures\pbr\architecture\stonewall01.dds");
        Assert.NotNull(rule);
        Assert.Equal(new Vector3(0.8f, 0.9f, 1.0f), rule!.ColorScale);
        Assert.Equal(0.5f, rule.RoughnessScale);

        var albedo = TextureFixture.Solid(200, 200, 200);
        // _rmaos: R=roughness (100 -> ~0.39), G=metallic (255 -> 1.0), B=AO (128 -> 0.5)
        var rmaos = TextureFixture.Solid(100, 255, 128);
        var plainLod = TextureImages.Decode(PbrLodConverter.Convert(albedo, 64, 1f));
        var bakedLod = TextureImages.Decode(PbrLodConverter.Convert(albedo, 64, 1f, rmaos, null, rule.ColorScale));
        // Metallic + AO + 0.8 red scale darkens baked diffuse meaningfully compared to unoccluded dielectric albedo
        Assert.True(bakedLod.Pixels[0] < plainLod.Pixels[0] * 0.6f, $"Expected darkened metallic+AO red channel: {bakedLod.Pixels[0]} vs {plainLod.Pixels[0]}");

        var normal = TextureFixture.Solid(128, 128, 255, 255);
        var glossNormal = TextureImages.Decode(PbrLodConverter.ConvertNormalWithGloss(normal, 64, rmaos, null, rule.RoughnessScale, rule.RoughnessOffset));
        // Expected roughness = (100/255)*0.5 + 0.1 = 0.296 -> gloss = 1 - 0.296 = 0.704 -> alpha ~ 180
        Assert.InRange(glossNormal.Pixels[3], 165, 195);
    }

    [Fact]
    public void BillboardRenderer_Preserves_NormalGloss_On_RenderedObjects_And_Blends_VertexAlpha_Overlays()
    {
        var basePart = TextureFixture.Card("stone", @"textures\stone.dds") with
        {
            Material = new LodMaterial([@"textures\stone.dds", @"textures\stone_n.dds", "", "", "", "", "", "", ""], 0, 0, 3, false, 0, 0, Vector3.Zero, 1f)
        };
        var overlayPart = TextureFixture.Card("moss", @"textures\moss.dds") with
        {
            Positions = [new(0, -1, 0), new(400, -1, 0), new(0, -1, 400), new(400, -1, 400)], // slightly in front of base
            Colors = [0x80FFFFFFu, 0x80FFFFFFu, 0x80FFFFFFu, 0x80FFFFFFu], // 50% vertex alpha
            Material = new LodMaterial([@"textures\moss.dds", "", "", "", "", "", "", "", ""], 8u, 0x20u, 3, true, 0x00ED, 0, Vector3.Zero, 1f) // alpha blend on, alpha test off
        };

        byte[] Fill(int w, int h, byte r, byte g, byte b, byte a)
        {
            var px = new byte[w * h * 4];
            for (int i = 0; i < px.Length; i += 4) { px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = a; }
            return px;
        }

        var rendered = BillboardRenderer.Render(
            new LodMesh { Path = "obj", Parts = [basePart, overlayPart] },
            path => path.Contains("stone_n") ? (Fill(64, 64, 128, 128, 255, 64), 64, 64)
                  : path.Contains("stone") ? (Fill(256, 256, 200, 40, 40, 255), 256, 256)
                  : (Fill(256, 256, 40, 200, 40, 255), 256, 256),
            maxPixels: 64, shaded: false, pixelSize: (32, 32))!;

        // 50% blend between red (200,40,40) and green (40,200,40) -> (~120, ~120, 40)
        int mid = (16 * 32 + 16) * 4;
        Assert.InRange(rendered.Rgba[mid], 105, 135);
        Assert.InRange(rendered.Rgba[mid + 1], 105, 135);
        // Normal gloss blended between stone_n (64) and moss default (255) -> ~160 (NOT clamped to 255!)
        Assert.InRange(rendered.NormalRgba![mid + 3], 140, 180);
    }

    [Fact]
    public void DdsAlpha_Detects_Bc1_PunchThrough_And_TextureRecipe_Allows_Negative_Multipliers()
    {
        var punch = new byte[16];
        for (int i = 0; i < 16; i += 4) { punch[i] = 180; punch[i + 1] = 120; punch[i + 2] = 90; punch[i + 3] = (byte)(i == 0 ? 0 : 255); }
        using var ms = new MemoryStream();
        DdsFile.WriteBc1(ms, 2, 2, [Bc1Encoder.EncodeImage(punch, 2, 2, punchThroughAlpha: true)]);
        var bc1Alpha = ms.ToArray();
        Assert.True(DdsAlpha.IsTranslucent(bc1Alpha));
        var repaired = DdsAlpha.MakeOpaque(bc1Alpha, 64);
        Assert.False(DdsAlpha.IsTranslucent(repaired));

        var negativeTile = TextureRecipe.ParseTile("96\t-3\t1\t1\ttextures\\green.dds\t1\t1\t0\t0\ttextures\\lod\\x.dds\t1\t1", false);
        Assert.Equal(-3f, negativeTile.Red);
        using var f = new TextureFixture();
        File.WriteAllText(f.RuleFile, "96\t-3\t1\t1\ttextures\\green.dds\t1\t1\t0\t0\ttextures\\lod\\majestic.dds\t1\t1");
        var res = TextureGeneration.Run(f.Request);
        Assert.True(res.Published);
        Assert.Equal(0, res.Errors);
        Assert.True(File.Exists(GamePath.ToFileSystemPath(f.Output, @"textures\lod\majestic.dds")));
    }

    [Fact]
    public void Mountain_Stamping_Raises_OcclusionGrid_Terrain_Only_Inside_Opaque_Mountain_Footprint()
    {
        using var f = new TextureFixture();
        var mtnPart = new LodMeshPart
        {
            Name = "mountain",
            Material = new LodMaterial([@"textures\red.dds", "", "", "", "", "", "", "", ""], 0, 0, 3, false, 0, 0, Vector3.Zero, 1f),
            Positions = [new(-1500, -1500, 0), new(1500, -1500, 0), new(0, 1500, 3000)],
            UVs = [new(0, 0), new(1, 0), new(0.5f, 1)],
            Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            Tangents = [Vector3.UnitX, Vector3.UnitX, Vector3.UnitX],
            Bitangents = [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY],
            Triangles = [0, 1, 2],
        };
        TextureFixture.Put(f.Data, @"meshes\mountains\peak_lod_0.nif", LodNifWriter.Write("peak", [mtnPart]));
        using var game = GameContext.Open(f.Game);
        var assets = game.BuildAssets(_ => true);

        var grid = new OcclusionGrid(-1, -1, 2, 2, 0f);
        var flat = new float[33 * 33];
        for (int cy = -1; cy <= 0; cy++)
            for (int cx = -1; cx <= 0; cx++) grid.SetTerrain(cx, cy, flat);

        var mtnRef = new LodReference("000801:Skyrim.esm", "peak", "Peak", "Tamriel", "Skyrim.esm",
            new Vector3(0, 0, 500), Vector3.Zero, 1f, new LodMeshSet(@"mountains\peak_lod_0.nif", null, null, null), LodReferenceFlags.None);

        bool stamped = OcclusionStage.StampOpaqueMountain(grid, assets, @"mountains\peak_lod_0.nif", mtnRef, 1f, new());
        Assert.True(stamped);
        // Center sample (0, 0) corresponds to gx = 32, gy = 32 -> inside triangle, raised well above bare 0!
        Assert.True(grid.TerrainAt(32, 32) > 1000f);
        // Far corner (-4000, 4000) -> gx = 0, gy = 63 -> outside triangle, stays bare 0!
        Assert.Equal(0f, grid.TerrainAt(0, 63));
    }
}
