using System.Numerics;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using PenguLOD.Core.Lod;
using PenguLOD.Core.World;
using PenguLOD.Meshes;
using PenguLOD.Meshes.Authoring;
using PenguLOD.Meshes.Nif;
using PenguLOD.Plugins;
using PenguLOD.Testing;
using Xunit;

namespace PenguLOD.Core.Tests;

public class Phase4ParityAndRulesTests
{
    [Fact]
    public void DynDolodRules_Enforce_ReferenceActions_NoMato_FlagDynamic_And_ObjectBillboards()
    {
        FormKey disabledKey = default, snowNoMatoKey = default, dynRuleKey = default, bbStatKey = default;
        FormKey bbBaseKey = default;
        using var f = new TerrainFixture(editMod: mod =>
        {
            var cell = mod.Worldspaces.Single().SubCells[0].Items[0].Items[0];
            var snowMat = mod.MaterialObjects.AddNew("SnowMaterialTest");

            Static MakeStat(string edid, string model, bool snow = false)
            {
                var st = mod.Statics.AddNew(edid);
                st.Model = new Model { File = model };
                if (snow) st.Material.SetTo(snowMat);
                return st;
            }

            var stDis = MakeStat("StatDisabledByRule", @"fixture\dis.nif");
            var stSnow = MakeStat("StatNoMato", @"fixture\snow.nif", snow: true);
            var stDyn = MakeStat("StatForceDyn", @"fixture\dyn.nif");
            var stBb = MakeStat("StatBillboard", @"fixture\statue.nif");
            bbBaseKey = stBb.FormKey;

            PlacedObject Place(Static st, float x)
            {
                var p = new PlacedObject(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE)
                { Placement = new Placement { Position = new P3Float(x, -10000, 0) } };
                p.Base.SetTo(st);
                cell.Temporary.Add(p);
                return p;
            }
            disabledKey = Place(stDis, -10000).FormKey;
            snowNoMatoKey = Place(stSnow, -9500).FormKey;
            dynRuleKey = Place(stDyn, -9000).FormKey;
            bbStatKey = Place(stBb, -8500).FormKey;
        });

        var data = f.Textures.Data;
        var cardBytes = LodNifWriter.Write("lod", [TextureFixture.Card("obj", @"textures\red.dds")]);
        foreach (var m in new[] { "dis", "snow", "dyn", "statue" })
        {
            TextureFixture.Put(data, $@"meshes\fixture\{m}.nif", cardBytes);
            TextureFixture.Put(data, $@"meshes\fixture\{m}_lod_0.nif", cardBytes);
        }
        // Provide a billboard for StatBillboard so Level0 at LOD4 + Billboard1 at LOD8 resolves!
        string bbPrefix = $@"textures\terrain\lodgen\skyrim.esm\statue_{bbBaseKey.ID:x8}";
        TextureFixture.Put(data, bbPrefix + ".dds", TextureFixture.Solid(100, 150, 200));
        TextureFixture.Put(data, bbPrefix + ".txt", Encoding.UTF8.GetBytes("Width=300\nHeight=500\nShiftZ=0\n"));

        var rules = new LodRules();
        rules.AddFile("test.ini", """
            [Skyrim LODGen]
            LODGen1=fixture\dis.nif,Level0,Level0,Level0,None,Disable,0
            LODGen2=fixture\snow.nif,Level0,Level0,Level0,None,Unchanged,4
            LODGen3=fixture\dyn.nif,Level0,Level0,Level0,None,Unchanged,8
            LODGen4=fixture\statue.nif,Level0,Billboard1,Billboard1,None,Unchanged,0
            LODGen5=\,Level0,Level1,Level2,None,Unchanged,0
            """);

        using var game = GameContext.Open(f.Game);
        var assets = game.BuildAssets(_ => true);
        var resolver = new LodMeshResolver(LodMeshIndex.Build(assets.EnumeratePaths(@"meshes\")), rules, assets.Exists);
        var scan = new MutagenReferenceScanner(game, assets, resolver).Scan(new(Worldspaces: ["FixtureWorld"], TreeLod: false, DynamicLod: true, GridObjects: false));

        // 1. Disable action omitted StatDisabledByRule
        Assert.DoesNotContain(scan.References, r => r.FormKey == disabledKey.ToString());
        // 2. FlagNoMato (4) suppressed Snow shader on StatNoMato
        var snowRef = Assert.Single(scan.References, r => r.FormKey == snowNoMatoKey.ToString());
        Assert.Equal(LodShaderKind.None, snowRef.Shader);
        // 3. FlagDynamic (8) routed StatForceDyn into Dynamic instead of static References
        Assert.DoesNotContain(scan.References, r => r.FormKey == dynRuleKey.ToString());
        Assert.Contains(scan.Dynamic!, d => d.Ref.FormKey == dynRuleKey.ToString());
        // 4. Billboard1 on STAT kept 3D mesh at LOD4 and emitted ~pengulod-treecard|far| at LOD8/16!
        var bbRef = Assert.Single(scan.References, r => r.FormKey == bbStatKey.ToString());
        Assert.EndsWith("statue_lod_0.nif", bbRef.Meshes.Lod4!);
        Assert.StartsWith(TreeCardSource.FarPrefix, bbRef.Meshes.Lod8!);
        Assert.StartsWith(TreeCardSource.FarPrefix, bbRef.Meshes.Lod16!);
    }

    [Fact]
    public void QuestSwitchScanner_Detects_ScriptToggled_Exterior_Refs_Without_FalsePositiving_QuestMarkers()
    {
        FormKey toggledByProp = default, plainMarkerRef = default;
        using var f = new TerrainFixture(editMod: mod =>
        {
            var cell = mod.Worldspaces.Single().SubCells[0].Items[0].Items[0];
            var st = mod.Statics.AddNew("QuestWall");
            st.Model = new Model { File = @"fixture\wall.nif" };

            var pToggle = new PlacedObject(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE)
            { Placement = new Placement { Position = new P3Float(-10000, -10000, 0) } };
            pToggle.Base.SetTo(st);
            cell.Temporary.Add(pToggle);
            toggledByProp = pToggle.FormKey;

            var pPlain = new PlacedObject(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE)
            { Placement = new Placement { Position = new P3Float(-9500, -10000, 0) } };
            pPlain.Base.SetTo(st);
            cell.Temporary.Add(pPlain);
            plainMarkerRef = pPlain.FormKey;

            var quest = mod.Quests.AddNew("TownRebuildQuest");
            quest.VirtualMachineAdapter = new QuestAdapter
            {
                Scripts =
                [
                    new ScriptEntry
                    {
                        Name = "TownStageRebuildScript",
                        Properties =
                        [
                            new ScriptObjectProperty { Name = "BarricadeRef", Object = pToggle.ToLink<ISkyrimMajorRecordGetter>() }
                        ]
                    },
                    new ScriptEntry
                    {
                        Name = "PlainQuestMarkerScript",
                        Properties =
                        [
                            new ScriptObjectProperty { Name = "LookTarget", Object = pPlain.ToLink<ISkyrimMajorRecordGetter>() }
                        ]
                    }
                ]
            };
        });

        using var game = GameContext.Open(f.Game);
        var switched = QuestSwitchScanner.Scan(game);
        Assert.Contains(toggledByProp, switched);
        Assert.DoesNotContain(plainMarkerRef, switched);
    }

    [Fact]
    public void MountainFit_Grid_Pulls_Exterior_Vertices_And_Leaves_Interior_Or_Far_Vertices_Untouched()
    {
        var fullPart = new LodMeshPart
        {
            Name = "mtn",
            Material = new LodMaterial([@"textures\red.dds"], 0, 0, 3, false, 0, 0, Vector3.Zero, 1f),
            Positions = [new(-500, -500, 0), new(500, -500, 0), new(0, 500, 0)],
            UVs = [Vector2.Zero, Vector2.One, Vector2.Zero],
            Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            Tangents = [Vector3.UnitX, Vector3.UnitX, Vector3.UnitX],
            Bitangents = [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY],
            Triangles = [0, 1, 2],
        };
        var lodPart = fullPart with
        {
            Positions =
            [
                new(0, 0, 25),     // 25u outside (+Z): pulled to (0, 0, 0)
                new(0, 0, -20),    // 20u inside (-Z): untouched
                new(0, 0, 150),    // > MaxMove (80u): untouched
            ]
        };
        var fitted = MountainFit.Fit(
            new LodMesh { Path = "lod", Parts = [lodPart] },
            new LodMesh { Path = "full", Parts = [fullPart] },
            out int moved);
        Assert.Equal(1, moved);
        Assert.Equal(new Vector3(0, 0, 0), fitted.Parts[0].Positions[0]);
        Assert.Equal(new Vector3(0, 0, -20), fitted.Parts[0].Positions[1]);
        Assert.Equal(new Vector3(0, 0, 150), fitted.Parts[0].Positions[2]);
    }

    [Fact]
    public void BuggedLargeRef_Emits_Dedicated_Lod4_MultiBoundNode_And_UndersidePluginWriter_Persists_FormIds()
    {
        var part = TextureFixture.Card("mtn", @"textures\red.dds");
        var mesh = new LodMesh { Path = @"meshes\mtn_lod_0.nif", Parts = [part] };
        var buggedRef = new LodReference("000800:Skyrim.esm", "000801:Skyrim.esm", "Mtn", "Tamriel", "Patch.esp",
            new Vector3(-5000, 8200, 0), Vector3.Zero, 1f,
            new LodMeshSet(@"meshes\mtn_lod_0.nif", null, null, null),
            LodReferenceFlags.BuggedLargeRef);

        var built = BtoBuilder.Build(new QuadKey("Tamriel", LodLevel.Lod4, -4, 0), [buggedRef], _ => mesh);
        // Cell of (-5000, 8200) is (-2, 2) -> "PL:largeref:4:-2:2" in NIF header string table
        Assert.Contains("PL:largeref:4:-2:2", Encoding.ASCII.GetString(built.Bytes));

        // FormID persistence across UndersidePluginWriter rebuilds
        using var tf = new TerrainFixture();
        string outDir = Path.Combine(tf.Root, "esp_out");
        uint firstId;
        using (var game = GameContext.Open(tf.Game))
        {
            var r1 = UndersidePluginWriter.Write(game, ["FixtureWorld"], outDir);
            using var overlay1 = SkyrimMod.CreateFromBinaryOverlay(r1.Path, SkyrimRelease.SkyrimSE);
            firstId = overlay1.MoveableStatics.Single().FormKey.ID;
            Assert.True(File.Exists(Path.Combine(outDir, UndersidePluginWriter.FormIdsFileName)));
        }
        using (var game = GameContext.Open(tf.Game))
        {
            var r2 = UndersidePluginWriter.Write(game, ["FixtureWorld"], outDir);
            using var overlay2 = SkyrimMod.CreateFromBinaryOverlay(r2.Path, SkyrimRelease.SkyrimSE);
            Assert.Equal(firstId, overlay2.MoveableStatics.Single().FormKey.ID);
        }
    }

    [Fact]
    public void BaseObjectSwapper_Evaluates_Deterministic_Model_Swaps_Seeded_ChanceS_And_Transforms()
    {
        FormKey refA = default, refB = default, swappedStatKey = default;
        using var f = new TerrainFixture(editMod: mod =>
        {
            var cell = mod.Worldspaces.Single().SubCells[0].Items[0].Items[0];
            var orig = mod.Statics.AddNew("OrigRock");
            orig.Model = new Model { File = @"fixture\orig.nif" };
            var repl = mod.Statics.AddNew("ReplRock");
            repl.Model = new Model { File = @"fixture\repl.nif" };
            swappedStatKey = repl.FormKey;

            var pA = new PlacedObject(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE)
            { Placement = new Placement { Position = new P3Float(-10000, -10000, 10) } };
            pA.Base.SetTo(orig); cell.Temporary.Add(pA); refA = pA.FormKey;

            var pB = new PlacedObject(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE)
            { Placement = new Placement { Position = new P3Float(-9500, -10000, 10) } };
            pB.Base.SetTo(orig); cell.Temporary.Add(pB); refB = pB.FormKey;
        });

        var data = f.Textures.Data;
        var cardBytes = LodNifWriter.Write("lod", [TextureFixture.Card("obj", @"textures\red.dds")]);
        foreach (var m in new[] { "orig", "repl" })
        {
            TextureFixture.Put(data, $@"meshes\fixture\{m}.nif", cardBytes);
            TextureFixture.Put(data, $@"meshes\fixture\{m}_lod_0.nif", cardBytes);
        }
        TextureFixture.Put(data, @"skse\plugins\po3_BaseObjectSwapper.dll", [0]);
        File.WriteAllText(Path.Combine(data, "Rock_SWAP.ini"), $"""
            [Forms]
            OrigRock|ReplRock
            [Transforms]
            0x{refA.ID:X}~Skyrim.esm|posR(0,0,250),scale(1.5)
            """);

        using var game = GameContext.Open(f.Game);
        var assets = game.BuildAssets(_ => true);
        var resolver = new LodMeshResolver(LodMeshIndex.Build(assets.EnumeratePaths(@"meshes\")), new LodRules(), assets.Exists);
        var scanner = new MutagenReferenceScanner(game, assets, resolver);
        var scan = scanner.Scan(new(Worldspaces: ["FixtureWorld"], TreeLod: false, DynamicLod: false, GridObjects: false));

        Assert.Equal(2, scan.References.Count);
        Assert.All(scan.References, r =>
        {
            Assert.Equal(swappedStatKey.ToString(), r.BaseFormKey);
            Assert.EndsWith("repl_lod_0.nif", r.Meshes.Lod4!);
        });
        var movedRef = Assert.Single(scan.References, r => r.FormKey == refA.ToString());
        Assert.Equal(260f, movedRef.Position.Z, 2);
        Assert.Equal(1.5f, movedRef.Scale, 3);
        Assert.Equal(2, scanner.BaseObjectSwaps!.SwappedReferences);
        Assert.Equal(1, scanner.BaseObjectSwaps.TransformedReferences);
    }

    [Fact]
    public void Mo2RootBuilder_Overlays_RootData_And_RootGameFiles_And_PluginIni_Loads_Extra_Bsas()
    {
        using var f = new TerrainFixture();
        string instanceDir = Path.Combine(f.Root, "mo2");
        string modsDir = Path.Combine(instanceDir, "mods");
        string profilesDir = Path.Combine(instanceDir, "profiles");
        string prof = Path.Combine(profilesDir, "Default");
        Directory.CreateDirectory(prof);

        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "+RootPack\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*CustomLand.esp\n");
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "Skyrim.esm\nccBGSSSE001-Fish.esm\nCustomLand.esp\n");

        // Stage Skyrim.ccc and Skyrim_Default.ini under <mod>/Root/, and a loose mesh + plugin INI under <mod>/Root/Data/!
        string modRoot = Path.Combine(modsDir, "RootPack");
        TextureFixture.Put(modRoot, @"Root\Skyrim.ccc", Encoding.UTF8.GetBytes("ccBGSSSE001-Fish.esm\n"));
        TextureFixture.Put(modRoot, @"Root\Skyrim_Default.ini", Encoding.UTF8.GetBytes("[Archive]\nsResourceArchiveList=Skyrim - Misc.bsa\n"));
        TextureFixture.Put(modRoot, @"Root\Data\meshes\rootbuilder\pillar_lod_0.nif", [1, 2, 3, 4]);
        TextureFixture.Put(modRoot, @"Root\Data\CustomLand.ini", Encoding.UTF8.GetBytes("[Archive]\nsResourceArchiveList=ExtraSharedAssets1.bsa, ExtraSharedAssets2.bsa\n"));

        var mo2 = PenguLOD.Plugins.Mo2.Mo2Instance.Open(instanceDir,
            new PenguLOD.Plugins.Mo2.Mo2Locations(GamePath: Path.GetDirectoryName(f.Textures.Data), ModsFolder: modsDir, ProfilesFolder: profilesDir));
        var profile = mo2.OpenProfile("Default");

        // 1. Root Builder Skyrim.ccc enabled ccBGSSSE001-Fish.esm implicitly!
        Assert.Contains("ccBGSSSE001-Fish.esm", profile.EnabledPlugins);
        Assert.NotNull(profile.ResolveGameRootFile("Skyrim_Default.ini"));

        // 2. Root Builder Root/Data/ loose file and root CustomLand.ini indexed in VirtualDataIndex!
        var vdi = PenguLOD.Plugins.Mo2.VirtualDataIndex.Build(profile, ["meshes"]);
        Assert.NotNull(vdi.ResolveLoose(@"meshes\rootbuilder\pillar_lod_0.nif"));
        var resolvedIni = vdi.ResolveRootFile("customland.ini");
        Assert.NotNull(resolvedIni);

        // 3. Plugin-INI BSA list parsed and selected in load order!
        var selectedBsas = SkyrimArchives.Select(GameRelease.SkyrimSE,
            [@"C:\Data\Skyrim - Misc.bsa", @"C:\Data\ExtraSharedAssets1.bsa", @"C:\Data\ExtraSharedAssets2.bsa", @"C:\Data\Unrelated.bsa"],
            ["Skyrim.esm", "CustomLand.esp"],
            ["Skyrim - Misc.bsa"],
            iniName => iniName.Equals("CustomLand.ini", StringComparison.OrdinalIgnoreCase) ? resolvedIni : null);
        Assert.Equal([@"C:\Data\Skyrim - Misc.bsa", @"C:\Data\ExtraSharedAssets1.bsa", @"C:\Data\ExtraSharedAssets2.bsa"], selectedBsas);
    }
}
