using System.Numerics;
using PenguLOD.Core.Occlusion;
using PenguLOD.Meshes;
using PenguLOD.Meshes.Authoring;
using PenguLOD.Core.World;
using PenguLOD.Core.Lod;
using PenguLOD.Plugins;
using PenguLOD.Testing;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace PenguLOD.Core.Tests;

public class OcclusionStageTests
{
    private static Worldspace World(SkyrimMod mod, FormKey key, out WorldspaceSubBlock sub)
    {
        var world = new Worldspace(key, SkyrimRelease.SkyrimSE) { EditorID = "FixtureWorld" };
        var block = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = (GroupTypeEnum)4 };
        sub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = (GroupTypeEnum)5 };
        block.Items.Add(sub); world.SubCells.Add(block); mod.Worldspaces.Add(world);
        return world;
    }

    [Fact]
    public void AnEarlierOcclusionPluginIsNotTheSourceOfCellData()
    {
        using var f = new TextureFixture();
        // Skyrim.esm: 8x8 hilly cells. ModA.esp edits one cell. An older Occlusion.esp, loaded last, still carries the pre-edit cell.
        var master = new SkyrimMod(ModKey.FromNameAndExtension("Skyrim.esm"), SkyrimRelease.SkyrimSE);
        var world = World(master, master.GetNextFormKey(), out var sub);
        world.LandDefaults = new WorldspaceLandDefaults { DefaultLandHeight = -2048, DefaultWaterHeight = -10000 };
        Cell? edited = null;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                var heights = new Array2d<sbyte>(new P2Int(33, 33), 0);
                for (int i = 1; i < 33; i++) heights[i, 0] = 4;
                var cell = new Cell(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { Grid = new CellGrid { Point = new P2Int(x, y) }, Name = "Original",
                    Landscape = new Landscape(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { VertexHeightMap = new LandscapeVertexHeightMap { Offset = 0, HeightMap = heights } } };
                sub.Items.Add(cell);
                if (x == 3 && y == 3) edited = cell;
            }
        master.WriteToBinary(Path.Combine(f.Data, "Skyrim.esm"));
        foreach (var (name, label) in new[] { ("ModA.esp", "Edited"), (OcclusionStage.FileName, "Stale") })
        {
            var mod = new SkyrimMod(ModKey.FromNameAndExtension(name), SkyrimRelease.SkyrimSE);
            World(mod, world.FormKey, out var overrides);
            overrides.Items.Add(new Cell(edited!.FormKey, SkyrimRelease.SkyrimSE) { Grid = new CellGrid { Point = new P2Int(3, 3) }, Name = label });
            mod.WriteToBinary(Path.Combine(f.Data, name));
        }
        File.WriteAllText(f.Plugins, "*ModA.esp\n*" + OcclusionStage.FileName + "\n");

        // Disposed before the fixture: Windows cannot delete plugins that are still open.
        using var game = GameContext.Open(f.Game);
        var terrain = TerrainReader.Read(game, ["FixtureWorld"]);
        var grids = new Dictionary<string, LodGrid> { { "FixtureWorld", new("FixtureWorld", new(-64, -64, 128, 4, 32)) } };
        string folder = Path.Combine(f.Root, "occlusion");
        var result = OcclusionStage.Run(game, terrain.Heights, grids, [], null, game.BuildAssets(), folder, new OcclusionSettings(Mode: "-Flat"), null, _ => { }, default);
        Assert.NotNull(result.Path);
        using var written = SkyrimMod.CreateFromBinaryOverlay(result.Path!, SkyrimRelease.SkyrimSE);
        var cells = written.Worldspaces.SelectMany(w => w.SubCells).SelectMany(b => b.Items).SelectMany(s => s.Items).ToList();
        Assert.Equal("Edited", Assert.Single(cells, c => c.FormKey == edited!.FormKey).Name?.String);
    }

    /// <summary>8x8 hilly cells of Skyrim.esm, the fixture the occlusion stage needs to write anything.</summary>
    private static (SkyrimMod Master, Worldspace World) HillyWorld()
    {
        var master = new SkyrimMod(ModKey.FromNameAndExtension("Skyrim.esm"), SkyrimRelease.SkyrimSE);
        var world = World(master, master.GetNextFormKey(), out var sub);
        world.LandDefaults = new WorldspaceLandDefaults { DefaultLandHeight = -2048, DefaultWaterHeight = -10000 };
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                var heights = new Array2d<sbyte>(new P2Int(33, 33), 0);
                for (int i = 1; i < 33; i++) heights[i, 0] = 4;
                sub.Items.Add(new Cell(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { Grid = new CellGrid { Point = new P2Int(x, y) },
                    Landscape = new Landscape(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { VertexHeightMap = new LandscapeVertexHeightMap { Offset = 0, HeightMap = heights } } });
            }
        return (master, world);
    }

    private static Static Statue(SkyrimMod mod, string model, bool empty = true) => new(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE)
    {
        EditorID = Path.GetFileNameWithoutExtension(model), Model = new Model { File = model },
        ObjectBounds = empty ? new ObjectBounds() : new ObjectBounds { First = new P3Int16(-1, -1, 0), Second = new P3Int16(1, 1, 1) },
    };

    [Fact]
    public void ACellKeepsItsOwnMaxHeightDataAndOneWithoutGetsTheBareLandscape()
    {
        using var f = new TextureFixture();
        var (master, world) = HillyWorld();
        // Cell (2, 2) carries max-height data as the base game's cells do, well above its ground; the others have none.
        var own = world.SubCells[0].Items[0].Items.Single(c => c.Grid!.Point == new P2Int(2, 2));
        var map = new Array2d<byte>(new byte[32, 32]); for (int i = 0; i < 32; i++) for (int j = 0; j < 32; j++) map[i, j] = 40;
        own.MaxHeightData = new CellMaxHeightData { Offset = 256, HeightMap = map };
        master.WriteToBinary(Path.Combine(f.Data, "Skyrim.esm"));
        File.WriteAllText(f.Plugins, "");

        using var game = GameContext.Open(f.Game);
        var terrain = TerrainReader.Read(game, ["FixtureWorld"]);
        var grids = new Dictionary<string, LodGrid> { { "FixtureWorld", new("FixtureWorld", new(-64, -64, 128, 4, 32)) } };
        // A tall object in cell (3, 3) must not raise that cell's surface: the computed data is the bare landscape.
        var tower = new LodReference("000900:Skyrim.esm", "tower", "Tower", "FixtureWorld", "Skyrim.esm", new(3 * 4096 + 2048, 3 * 4096 + 2048, 0), default, 1,
            new LodMeshSet(@"test\tower.nif", null, null, null), LodReferenceFlags.None);
        TextureFixture.Put(f.Data, @"meshes\test\tower.nif", LodNifWriter.Write("tower", [TextureFixture.Card("tower", @"textures\red.dds") with
            { Positions = [new(-200, 0, 0), new(200, 0, 0), new(-200, 0, 8000), new(200, 0, 8000)] }]));
        var result = OcclusionStage.Run(game, terrain.Heights, grids, [tower], null, game.BuildAssets(), Path.Combine(f.Root, "occlusion"), new OcclusionSettings(Mode: "-Flat"), null, _ => { }, default);
        using var written = SkyrimMod.CreateFromBinaryOverlay(result.Path!, SkyrimRelease.SkyrimSE);
        var cells = written.Worldspaces.SelectMany(w => w.SubCells).SelectMany(b => b.Items).SelectMany(s => s.Items).ToDictionary(c => c.Grid!.Point);
        var kept = cells[new P2Int(2, 2)].MaxHeightData!;
        Assert.Equal(256f, kept.Offset); Assert.All(Enumerable.Range(0, 32), i => Assert.Equal(40, kept.HeightMap[i, i]));
        // Every cell has the same hill, so the cell with the tower gets exactly the surface of one without.
        var computed = cells[new P2Int(3, 3)].MaxHeightData!; var bare = cells[new P2Int(5, 5)].MaxHeightData!;
        Assert.Equal(bare.Offset, computed.Offset);
        Assert.Equal(bare.HeightMap.Select(p => p.Value), computed.HeightMap.Select(p => p.Value));
    }

    [Fact]
    public void StaticsThatStandOutdoorsWithEmptyBoundsGetTheirModelsBounds()
    {
        using var f = new TextureFixture();
        var (master, _) = HillyWorld();
        master.WriteToBinary(Path.Combine(f.Data, "Skyrim.esm"));
        // A 300 x 200 x 500 house, and a pebble under 24 units on every axis.
        var house = TextureFixture.Card("house", @"textures\red.dds") with
            { Positions = [new(-150, -100, 0), new(150, -100, 0), new(-150, 100, 500), new(150, 100, 500)] };
        var pebble = TextureFixture.Card("pebble", @"textures\red.dds") with
            { Positions = [new(0, 0, 0), new(10, 0, 0), new(0, 10, 10), new(10, 10, 10)] };
        TextureFixture.Put(f.Data, @"meshes\test\house.nif", LodNifWriter.Write("house", [house]));
        TextureFixture.Put(f.Data, @"meshes\test\pebble.nif", LodNifWriter.Write("pebble", [pebble]));
        var mod = new SkyrimMod(ModKey.FromNameAndExtension("Houses.esp"), SkyrimRelease.SkyrimSE);
        var outdoor = Statue(mod, @"test\house.nif"); var indoor = Statue(mod, @"test\house.nif"); var tiny = Statue(mod, @"test\pebble.nif");
        var bounded = Statue(mod, @"test\house.nif", empty: false); var noModel = Statue(mod, @"test\missing.nif");
        foreach (var s in new[] { outdoor, indoor, tiny, bounded, noModel }) mod.Statics.Add(s);
        mod.WriteToBinary(Path.Combine(f.Data, "Houses.esp"));
        File.WriteAllText(f.Plugins, "*Houses.esp\n");

        using var game = GameContext.Open(f.Game);
        var terrain = TerrainReader.Read(game, ["FixtureWorld"]);
        var grids = new Dictionary<string, LodGrid> { { "FixtureWorld", new("FixtureWorld", new(-64, -64, 128, 4, 32)) } };
        var outdoors = new HashSet<FormKey> { outdoor.FormKey, tiny.FormKey, bounded.FormKey, noModel.FormKey };
        var result = OcclusionStage.Run(game, terrain.Heights, grids, [], null, game.BuildAssets(), Path.Combine(f.Root, "occlusion"), new OcclusionSettings(Mode: "-Flat"), null, _ => { },
            default, outdoors);
        Assert.Equal(new BoundsFixStage.Result(3, 1, 1, 1, 0, []), result.Bounds! with { LeftOutPlugins = [] });
        using var written = SkyrimMod.CreateFromBinaryOverlay(result.Path!, SkyrimRelease.SkyrimSE);
        var fixedStatic = Assert.Single(written.Statics);
        Assert.Equal(outdoor.FormKey, fixedStatic.FormKey);
        Assert.Equal((new P3Int16(-150, -100, 0), new P3Int16(150, 100, 500)), (fixedStatic.ObjectBounds.First, fixedStatic.ObjectBounds.Second));
        string model = fixedStatic.Model!.File;
        Assert.EndsWith(@"test\house.nif", model, StringComparison.OrdinalIgnoreCase);                                   // the rest of the record is the winning one
        Assert.Contains(written.ModHeader.MasterReferences, m => m.Master.FileName == "Houses.esp");
    }

    [Fact]
    public void StaticsThatWouldTakeTheOcclusionPluginOverTheMasterLimitAreLeftAloneAndReported()
    {
        using var f = new TextureFixture();
        var (master, _) = HillyWorld();
        master.WriteToBinary(Path.Combine(f.Data, "Skyrim.esm"));
        TextureFixture.Put(f.Data, @"meshes\test\house.nif", LodNifWriter.Write("house", [TextureFixture.Card("house", @"textures\red.dds")]));
        // One plugin more than the limit allows, each with one static with empty bounds. The first holds two, so it is taken first; the
        // last ones in name order are left out.
        var names = Enumerable.Range(0, BoundsFixStage.MaxMasters + 1).Select(i => $"Mod{i:D3}.esp").ToArray();
        var outdoors = new HashSet<FormKey>();
        foreach (var name in names)
        {
            var mod = new SkyrimMod(ModKey.FromNameAndExtension(name), SkyrimRelease.SkyrimSE) { IsSmallMaster = true };
            for (int i = 0; i < (name == names[0] ? 2 : 1); i++) { var s = Statue(mod, @"test\house.nif"); mod.Statics.Add(s); outdoors.Add(s.FormKey); }
            mod.WriteToBinary(Path.Combine(f.Data, name));
        }
        File.WriteAllText(f.Plugins, string.Concat(names.Select(n => "*" + n + "\n")));

        using var game = GameContext.Open(f.Game);
        var terrain = TerrainReader.Read(game, ["FixtureWorld"]);
        var grids = new Dictionary<string, LodGrid> { { "FixtureWorld", new("FixtureWorld", new(-64, -64, 128, 4, 32)) } };
        var warnings = new List<string>();
        var result = OcclusionStage.Run(game, terrain.Heights, grids, [], null, game.BuildAssets(), Path.Combine(f.Root, "occlusion"), new OcclusionSettings(Mode: "-Flat"), null, warnings.Add,
            default, outdoors);
        // Skyrim.esm is already a master (the cells), so 249 plugins fit: the first one's two statics and one each from 248 more.
        Assert.Equal(BoundsFixStage.MaxMasters, result.Bounds!.Fixed);
        Assert.Equal(2, result.Bounds.OverMasterLimit);
        Assert.Equal([names[^2], names[^1]], result.Bounds.LeftOutPlugins!, StringComparer.OrdinalIgnoreCase);
        using var written = SkyrimMod.CreateFromBinaryOverlay(result.Path!, SkyrimRelease.SkyrimSE);
        Assert.Equal(BoundsFixStage.MaxMasters, written.ModHeader.MasterReferences.Count);
        Assert.Contains(warnings, w => w.Contains("master", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheWorldspacesPersistentCellGetsNoOcclusionData(bool flagged)
    {
        using var f = new TextureFixture();
        // 8x8 hilly cells and the persistent cell, which carries the coordinates of cell (0, 0) as the game's own do.
        var master = new SkyrimMod(ModKey.FromNameAndExtension("Skyrim.esm"), SkyrimRelease.SkyrimSE);
        var world = World(master, master.GetNextFormKey(), out var sub);
        world.LandDefaults = new WorldspaceLandDefaults { DefaultLandHeight = -2048, DefaultWaterHeight = -10000 };
        world.TopCell = new Cell(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { Grid = new CellGrid { Point = new P2Int(0, 0) }, MajorRecordFlagsRaw = flagged ? 0x400 : 0 };
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                var heights = new Array2d<sbyte>(new P2Int(33, 33), 0);
                for (int i = 1; i < 33; i++) heights[i, 0] = 4;
                sub.Items.Add(new Cell(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { Grid = new CellGrid { Point = new P2Int(x, y) },
                    Landscape = new Landscape(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { VertexHeightMap = new LandscapeVertexHeightMap { Offset = 0, HeightMap = heights } } });
            }
        master.WriteToBinary(Path.Combine(f.Data, "Skyrim.esm"));
        File.WriteAllText(f.Plugins, "");

        using var game = GameContext.Open(f.Game);
        var terrain = TerrainReader.Read(game, ["FixtureWorld"]);
        var grids = new Dictionary<string, LodGrid> { { "FixtureWorld", new("FixtureWorld", new(-64, -64, 128, 4, 32)) } };
        var result = OcclusionStage.Run(game, terrain.Heights, grids, [], null, game.BuildAssets(), Path.Combine(f.Root, "occlusion"), new OcclusionSettings(Mode: "-Flat"), null, _ => { }, default);
        Assert.Equal(64, result.Cells);
        using var written = SkyrimMod.CreateFromBinaryOverlay(result.Path!, SkyrimRelease.SkyrimSE);
        var worldspace = Assert.Single(written.Worldspaces);
        Assert.Null(worldspace.TopCell);
        var cells = worldspace.SubCells.SelectMany(b => b.Items).SelectMany(s => s.Items).ToList();
        Assert.Equal(64, cells.Count);
        Assert.DoesNotContain(cells, c => c.FormKey == world.TopCell.FormKey);
        Assert.All(cells, c => Assert.NotNull(c.OcclusionData));
    }

    [Fact]
    public void AModeNamesTheCellsItTakesAndDynDolodsSettingsFileGivesEachWorldspaceItsOwn()
    {
        Assert.Equal("All", OcclusionCellMode.Parse("all").ToString());
        Assert.Equal("-TVDT -Flat +Border", OcclusionCellMode.Parse("+border  -flat -TVDT").ToString());
        Assert.All(OcclusionWorldRules.Choices.Skip(1), c => Assert.Equal(c, OcclusionCellMode.Parse(c).ToString()));
        foreach (string bad in new[] { "", "+Flat", "+TVDT -TVDT", "All -Flat", "-Flat -Flat" }) Assert.Throws<ArgumentException>(() => OcclusionCellMode.Parse(bad));
        var mode = OcclusionCellMode.Parse("-TVDT -Flat +Border");
        Assert.True(mode.Includes(hasData: false, flat: false, inBorder: true));
        Assert.False(mode.Includes(true, false, true)); Assert.False(mode.Includes(false, true, true)); Assert.False(mode.Includes(false, false, false));
        Assert.True(OcclusionCellMode.Parse("+TVDT").Includes(true, true, false));

        // What DynDOLOD ships with: the general mode, and Tamriel, Sovngarde and the Soul Cairn with their own.
        var shipped = OcclusionWorldRules.For(OcclusionWorldRules.DynDolod);
        Assert.Equal("-TVDT -Flat +Border", shipped.ModeFor("DLC2SolstheimWorld").ToString());
        Assert.Equal("-Flat +Border", shipped.ModeFor("tamriel").ToString());
        Assert.Equal("-TVDT -Flat", shipped.ModeFor("DLC01SoulCairn").ToString());
        Assert.False(shipped.Enabled("ArnimaDagonRealm")); Assert.Null(shipped.Enabled("Tamriel"));

        var read = OcclusionWorldRules.Read(["[DynDOLOD]", "; OcclusionMode=All", "OcclusionMode=-Flat", "OcclusionMode_Tamriel=+TVDT", "OcclusionMode_Broken=sideways",
            "Occlusion_SmallIsland=1", "Occlusion_Other=0", "OcclusionRadius=100", "OcclusionMaxThreadsObjectLOD=4"]);
        Assert.Equal("-Flat", read.ModeFor("Anywhere").ToString()); Assert.Equal("+TVDT", read.ModeFor("Tamriel").ToString());
        Assert.Equal("-Flat", read.ModeFor("Broken").ToString());
        Assert.True(read.Enabled("SmallIsland")); Assert.False(read.Enabled("Other")); Assert.Null(read.Enabled("MaxThreadsObjectLOD"));
        Assert.Equal("All", OcclusionWorldRules.For("All").ModeFor("Tamriel").ToString());
    }

    /// <summary>
    /// The hilly world with a border region: cells with x below 4 are linked to it, the row y = 0 already has occlusion data, and cell
    /// (2, 5) is flat. A city worldspace that draws this world's LOD has two cells, one of them with occlusion data.
    /// </summary>
    private static (SkyrimMod Master, Worldspace World, Worldspace City) WorldWithABorderAndACity()
    {
        var (master, world) = HillyWorld();
        var border = new Region(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "BorderRegionFixture", MajorFlags = Region.MajorFlag.BorderRegion };
        var other = new Region(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "WeatherRegionFixture" };
        master.Regions.Add(border); master.Regions.Add(other);
        foreach (var cell in world.SubCells[0].Items[0].Items)
        {
            var p = cell.Grid!.Point;
            cell.Regions = [(p.X < 4 ? border : other).ToLink<IRegionGetter>()];
            if (p.Y == 0) cell.OcclusionData = new byte[684];
            if (p == new P2Int(2, 5)) cell.Landscape!.VertexHeightMap = new LandscapeVertexHeightMap { Offset = 0, HeightMap = new Array2d<sbyte>(new P2Int(33, 33), 0) };
        }
        var city = new Worldspace(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "FixtureCity",
            Parent = new WorldspaceParent { Worldspace = world.ToLink<IWorldspaceGetter>(), Flags = WorldspaceParent.Flag.UseLodData } };
        var block = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = (GroupTypeEnum)4 };
        var sub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = (GroupTypeEnum)5 };
        block.Items.Add(sub); city.SubCells.Add(block); master.Worldspaces.Add(city);
        sub.Items.Add(new Cell(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { Grid = new CellGrid { Point = new P2Int(6, 6) }, OcclusionData = new byte[684] });
        sub.Items.Add(new Cell(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { Grid = new CellGrid { Point = new P2Int(6, 7) } });
        return (master, world, city);
    }

    private static Dictionary<(string World, int X, int Y), ICellGetter> RunWithMode(TextureFixture f, string? mode, OcclusionWorldRules? rules = null)
    {
        using var game = GameContext.Open(f.Game);
        var terrain = TerrainReader.Read(game, ["FixtureWorld"]);
        var grids = new Dictionary<string, LodGrid> { { "FixtureWorld", new("FixtureWorld", new(-64, -64, 128, 4, 32)) } };
        string folder = Path.Combine(f.Root, "occlusion-" + Guid.NewGuid().ToString("N"));
        var settings = mode is null ? new OcclusionSettings() : new OcclusionSettings(Mode: mode);
        var result = OcclusionStage.Run(game, terrain.Heights, grids, [], null, game.BuildAssets(), folder, settings, null, _ => { }, default, rules: rules);
        if (result.Path is null) return [];
        var written = SkyrimMod.CreateFromBinary(result.Path, SkyrimRelease.SkyrimSE);
        var names = new Dictionary<FormKey, string>();
        using (var master = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(f.Data, "Skyrim.esm"), SkyrimRelease.SkyrimSE))
            foreach (var w in master.Worldspaces) names[w.FormKey] = w.EditorID!;
        var cells = written.Worldspaces.SelectMany(w => w.SubCells.SelectMany(b => b.Items).SelectMany(s => s.Items).Select(c => (World: names[w.FormKey], Cell: (ICellGetter)c)))
            .ToDictionary(c => (c.World, c.Cell.Grid!.Point.X, c.Cell.Grid.Point.Y), c => c.Cell);
        Assert.Equal(cells.Count, result.Cells);
        return cells;
    }

    [Fact]
    public void TheModeDecidesWhichCellsGetOcclusionDataAndCityCellsGetTheirParents()
    {
        using var f = new TextureFixture();
        var (master, _, _) = WorldWithABorderAndACity();
        master.WriteToBinary(Path.Combine(f.Data, "Skyrim.esm"));
        File.WriteAllText(f.Plugins, "");
        static int Own(Dictionary<(string World, int X, int Y), ICellGetter> cells) => cells.Keys.Count(k => k.World == "FixtureWorld");

        // DynDOLOD's general mode, "-TVDT -Flat +Border": the border half (32 cells) less its row with data (4) and the flat cell.
        var shipped = RunWithMode(f, null);
        Assert.Equal(27, Own(shipped));
        Assert.All(shipped.Keys.Where(k => k.World == "FixtureWorld"), k => Assert.True(k.X < 4 && k.Y > 0 && (k.X, k.Y) != (2, 5)));
        // The city is not in the border region; of its two cells the one without data is taken, and it gets the parent's data.
        Assert.False(shipped.ContainsKey(("FixtureCity", 6, 6)));
        var all = RunWithMode(f, "All");
        Assert.Equal(64, Own(all));
        Assert.Equal(all[("FixtureWorld", 6, 7)].OcclusionData!.Value.ToArray(), shipped[("FixtureCity", 6, 7)].OcclusionData!.Value.ToArray());
        Assert.Equal(all[("FixtureWorld", 6, 6)].OcclusionData!.Value.ToArray(), all[("FixtureCity", 6, 6)].OcclusionData!.Value.ToArray());
        Assert.Contains(all[("FixtureWorld", 6, 6)].OcclusionData!.Value.ToArray(), b => b != 0);

        Assert.Equal(63, Own(RunWithMode(f, "-Flat")));
        Assert.Equal(32, Own(RunWithMode(f, "+Border")));
        var withData = RunWithMode(f, "+TVDT");
        Assert.Equal(8, Own(withData)); Assert.All(withData.Keys.Where(k => k.World == "FixtureWorld"), k => Assert.Equal(0, k.Y));
        Assert.True(withData.ContainsKey(("FixtureCity", 6, 6))); Assert.False(withData.ContainsKey(("FixtureCity", 6, 7)));
        Assert.Equal(56, Own(RunWithMode(f, "-TVDT")));

        // A worldspace's own mode and its switch in DynDOLOD's settings file.
        Assert.Equal(28, Own(RunWithMode(f, null, OcclusionWorldRules.Read(["OcclusionMode=All", "OcclusionMode_FixtureWorld=-TVDT +Border"]))));
        Assert.Empty(RunWithMode(f, null, OcclusionWorldRules.Read(["OcclusionMode=All", "Occlusion_FixtureWorld=0"])));
        var noCity = RunWithMode(f, null, OcclusionWorldRules.Read(["OcclusionMode=All", "Occlusion_FixtureCity=0"]));
        Assert.Equal(64, noCity.Count);
    }

    [Fact]
    public void AWorldspaceFlaggedAsSmallIsLeftAloneUnlessItIsSwitchedOnByName()
    {
        using var f = new TextureFixture();
        var (master, world) = HillyWorld();
        world.Flags |= Worldspace.Flag.SmallWorld;
        master.WriteToBinary(Path.Combine(f.Data, "Skyrim.esm"));
        File.WriteAllText(f.Plugins, "");
        Assert.Empty(RunWithMode(f, "All"));
        Assert.Equal(64, RunWithMode(f, null, OcclusionWorldRules.Read(["OcclusionMode=All", "Occlusion_FixtureWorld=1"])).Count);
    }
}
