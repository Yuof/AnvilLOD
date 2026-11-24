using System.Buffers.Binary;
using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using PenguLOD.Core.Lod;
using PenguLOD.Core.Pipeline;
using PenguLOD.Core.World;
using PenguLOD.Meshes;
using PenguLOD.Meshes.Nif;
using PenguLOD.Plugins;
using PenguLOD.Plugins.Mo2;
using PenguLOD.Testing;
using PenguLOD.Textures;

namespace PenguLOD.Core.Tests;

public sealed class Phase1BugfixTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pengulod-phase1-" + Guid.NewGuid().ToString("N"));
    public Phase1BugfixTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void SkyrimArchives_Select_PlacesPluginBaseBsaBeforeSuffixedTexturesBsa()
    {
        var selected = SkyrimArchives.Select(
            GameRelease.SkyrimSE,
            [@"Data\ModB - Textures.bsa", @"Data\ModB.bsa", @"Data\ModB - Meshes.bsa"],
            ["ModB.esp"],
            []);
        Assert.Equal([@"Data\ModB.bsa", @"Data\ModB - Meshes.bsa", @"Data\ModB - Textures.bsa"], selected);
    }

    [Fact]
    public void GlowWindows_AreNotMarkedAsGridObjects_InDynamicLodFlags()
    {
        var dummyRef = new LodReference("000800:Test.esp", "000801:Test.esp", "House", "Tamriel", "Test.esp",
            Vector3.Zero, Vector3.Zero, 1f, new LodMeshSet("houseglow_lod_0.nif", null, null, null), LodReferenceFlags.None);
        var glow = new DynamicLodReference(dummyRef, "Test.esp", 0x800, "Skyrim.esm", 0x3C, Grid: DynamicGrid.Near, Glow: true);
        Assert.False(glow.IsGridObject);
        uint flags = DynamicLodWriter.Flags(glow);
        Assert.NotEqual(0u, flags & 8u); // kNearGrid preserved
        Assert.NotEqual(0u, flags & 32u); // kGlow set
    }

    [Fact]
    public void StaticObjectLod_RetainsStaticGeometry_WhenModelHasParticleSystemChild()
    {
        byte[] baseNif = ModelNifFixture.Create(ModelNifFixture.Geometry.Packed);
        var nif = NifFile.Read(baseNif);
        var w = new NifWriter();
        foreach (string s in nif.Strings) w.String(s);
        foreach (var block in nif.Blocks) w.Reserve(block.Type);
        foreach (var block in nif.Blocks) w.Set(block.Index, b => b.Write(baseNif, block.Offset, block.Size));
        int particle = w.Reserve("NiParticleSystem");
        w.Set(particle, b =>
        {
            NifWriter.WriteObjectNet(b, -1);
            NifWriter.WriteAvObject(b, 0, Vector3.Zero, 1f); // unhidden particle system
        });
        w.Set(0, b =>
        {
            NifWriter.WriteObjectNet(b, -1);
            NifWriter.WriteAvObject(b, 14, Vector3.Zero, 1f);
            b.Write(2u); b.Write(3); b.Write(particle); b.Write(0u);
        });
        byte[] withParticle = w.ToBytes(0);

        Assert.True(NifGeometryReader.Read("barrel.nif", withParticle, staticObjectLod: false).HasIncompleteGeometry);
        var staticMesh = NifGeometryReader.Read("barrel.nif", withParticle, staticObjectLod: true);
        Assert.False(staticMesh.HasIncompleteGeometry);
        Assert.Single(staticMesh.Parts);
        Assert.StartsWith(NifGeometryReader.LodOmissionWarning, Assert.Single(staticMesh.Warnings));
    }

    [Fact]
    public void TerrainHeightmap_ClampsExtremeHeightsAndFillsUncoveredCells_WhenWarnAndFallbackProvided()
    {
        var warnings = new List<string>();
        var map = TerrainHeightmap.Export("ExtremeWorld", 0, 0, 1, 1, 64,
            (wx, wy) => wx < 4096f ? 500_000f : null,
            fallbackHeight: -1024f, warn: warnings.Add);

        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("clamped"));
        Assert.Contains(warnings, w => w.Contains("filled"));
        ushort topLeft = BinaryPrimitives.ReadUInt16LittleEndian(map.Bytes.AsSpan(148, 2));
        Assert.Equal(ushort.MaxValue, topLeft);
    }

    [Fact]
    public void OcclusionStage_RotatedBounds_AccountsForPitchAndRoll()
    {
        // Tall thin pillar (radius 10, height 1000) pitched 90 degrees onto its side:
        // horizontal extent becomes ~1000 and top drops to ~10.
        var (minX, minY, maxX, maxY, maxZ) = OcclusionStage.RotatedBounds(
            new Vector3(-10f, -10f, 0f), new Vector3(10f, 10f, 1000f),
            new Vector3(MathF.PI * 0.5f, 0f, 0f), 1f);
        float spanXY = MathF.Max(maxX - minX, maxY - minY);
        Assert.InRange(spanXY, 990f, 1010f);
        Assert.InRange(maxZ, 9f, 11f);
    }

    [Fact]
    public void SeasonalLod_LinkOrCopy_CreatesHardLinkOnSameFilesystem()
    {
        string src = Path.Combine(_root, "src.bto");
        string dst = Path.Combine(_root, "sub", "dst.WIN.bto");
        File.WriteAllBytes(src, [1, 2, 3, 4, 5]);
        SeasonalLod.LinkOrCopy(src, dst);
        Assert.Equal([1, 2, 3, 4, 5], File.ReadAllBytes(dst));
        if (OperatingSystem.IsLinux())
        {
            // Modifying src in-place updates dst iff they share the same inode.
            using (var fs = new FileStream(src, FileMode.Open, FileAccess.Write)) fs.WriteByte(99);
            Assert.Equal(99, File.ReadAllBytes(dst)[0]);
        }
    }

    [Fact]
    public void Mo2Profile_ReadPlugins_IncludesSkyrimVrMasterAndCcWithoutLoadorderTxt()
    {
        string vrGame = Path.Combine(_root, "vrgame"), vrProfile = Path.Combine(_root, "vrprof");
        Directory.CreateDirectory(vrGame); Directory.CreateDirectory(vrProfile);
        File.WriteAllBytes(Path.Combine(vrGame, "SkyrimVR.exe"), []);
        File.WriteAllText(Path.Combine(vrGame, "Skyrim.ccc"), "ccBGSSSE001-Fish.esm\n");
        File.WriteAllText(Path.Combine(vrProfile, "plugins.txt"), "*MyVrMod.esp\n");
        var vrPlugins = Mo2Profile.ReadPlugins(vrProfile, vrGame, out _);
        Assert.Contains("SkyrimVR.esm", vrPlugins);
        Assert.DoesNotContain("ccBGSSSE001-Fish.esm", vrPlugins);

        string seGame = Path.Combine(_root, "segame"), seProfile = Path.Combine(_root, "seprof");
        Directory.CreateDirectory(seGame); Directory.CreateDirectory(seProfile);
        File.WriteAllText(Path.Combine(seGame, "skyrim.ccc"), "ccBGSSSE001-Fish.esm\n"); // lowercase on Linux
        File.WriteAllText(Path.Combine(seProfile, "plugins.txt"), "*MySeMod.esp\n");
        var sePlugins = Mo2Profile.ReadPlugins(seProfile, seGame, out _);
        int ccIdx = sePlugins.IndexOf("ccBGSSSE001-Fish.esm");
        int modIdx = sePlugins.IndexOf("MySeMod.esp");
        Assert.True(ccIdx >= 0 && ccIdx < modIdx);
    }

    [Fact]
    public void TerrainSource_UsesPaintersLayerBlend_AndLinearizedVertexColors()
    {
        // Verify SrgbToLinear(0.5) ~ 0.214 rather than raw 0.5 linear multiplier.
        var lin = TerrainSource.SrgbToLinear(new Vector3(0.5f, 0.5f, 0.5f));
        Assert.InRange(lin.X, 0.21f, 0.23f);

        // Verify painter's blend: base red (1,0,0), layer 1 green at 0.8, layer 2 blue at 1.0 -> 100% blue (0,0,1), not diluted!
        using var fixture = new TerrainFixture(editMod: mod =>
        {
            var greenSet = mod.TextureSets.AddNew(); greenSet.Diffuse = @"green.dds"; greenSet.NormalOrGloss = @"green_n.dds";
            var blueSet = mod.TextureSets.AddNew(); blueSet.Diffuse = @"blue.dds"; blueSet.NormalOrGloss = @"blue_n.dds";
            var greenLtex = mod.LandscapeTextures.AddNew(); greenLtex.TextureSet.SetTo(greenSet);
            var blueLtex = mod.LandscapeTextures.AddNew(); blueLtex.TextureSet.SetTo(blueSet);
            foreach (var cell in mod.Worldspaces.Single().SubCells.SelectMany(b => b.Items).SelectMany(b => b.Items))
                for (int q = 0; q < 4; q++)
                {
                    var a0 = new AlphaLayer { Header = new LayerHeader { Quadrant = (Quadrant)q, LayerNumber = 1 }, AlphaLayerData = [] };
                    a0.Header.Texture.SetTo(greenLtex);
                    var a1 = new AlphaLayer { Header = new LayerHeader { Quadrant = (Quadrant)q, LayerNumber = 2 }, AlphaLayerData = [] };
                    a1.Header.Texture.SetTo(blueLtex);
                    for (ushort i = 0; i < 289; i++)
                    {
                        a0.AlphaLayerData.Add(new AlphaLayerData { Position = i, Opacity = 0.8f });
                        a1.AlphaLayerData.Add(new AlphaLayerData { Position = i, Opacity = 1.0f });
                    }
                    cell.Landscape!.Layers.Add(a0);
                    cell.Landscape!.Layers.Add(a1);
                }
        });
        TextureFixture.Put(fixture.Textures.Data, @"textures\blue.dds", TextureFixture.Solid(0, 0, 255));
        TextureFixture.Put(fixture.Textures.Data, @"textures\blue_n.dds", TextureFixture.Solid(128, 128, 255));
        var result = TerrainGeneration.Run(fixture.Request);
        var tile = TextureImages.Decode(File.ReadAllBytes(GamePath.ToFileSystemPath(fixture.Output, @"textures\terrain\FixtureWorld\FixtureWorld.4.-4.-4.dds")));
        Assert.InRange(tile.Pixels[0], 0, 15);
        Assert.InRange(tile.Pixels[1], 0, 15);
        Assert.InRange(tile.Pixels[2], 240, 255);
    }
}
