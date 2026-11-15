using PenguLOD.Core.Occlusion;

namespace PenguLOD.Core.Tests;

public sealed class TvdtDataTests
{
    // The TVDT xLODGen wrote for Tamriel cell (0, 0) of a single-chunk run; its debug image for the same cell (visible LOD4 blocks) matches the
    // decoded leaves pixel for pixel (185 visible, the cell's own block among them).
    private const string Golden =
        "030000000000000000000000000000000000000000000000000000000000000000000000000000000000800100000000000000018809000000" +
        "00000000000000040000cb063f0020beffaf01d83b0009fc3ff61f2c100000000000000000000004000041051f000000000000000000000000" +
        "0000001000000415000000000000befbf9fffe3f181bbcbffeffff030000fceb6f0000f09fffedff7f0000101c00fe63c70024f057d02e3000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000008" +
        "00000000000000000000000000000000000000000000000000000000000000000000000000000000000006000001c81500002c070000000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";

    [Fact]
    public void Nodes_are_numbered_the_way_the_game_numbers_them()
    {
        // The game gives a node its number and then builds its four children: (x, y), (x, y + half), (x + half, y), (x + half, y + half).
        var t = new TvdtData();
        Assert.Equal(64, t.LeavesPerSide);
        Assert.Equal(6, t.TopLevel);
        Assert.Equal(684, t.ByteCount);
        Assert.Equal(0, t.NodeBit(6, 0, 0));        // the root comes first
        Assert.Equal(1, t.NodeBit(5, 0, 0));        // then its first child, and so on down
        Assert.Equal(5, t.NodeBit(1, 0, 0));
        Assert.Equal(6, t.LeafBit(0, 0));
        Assert.Equal(7, t.LeafBit(0, 1));           // Z order, Y is the low bit
        Assert.Equal(8, t.LeafBit(1, 0));
        Assert.Equal(9, t.LeafBit(1, 1));
        Assert.Equal(10, t.NodeBit(1, 0, 1));       // the next 8-cell node comes before its own blocks (11..14)
        Assert.Equal(25, t.NodeBit(2, 0, 1));
        Assert.Equal(1366, t.NodeBit(5, 0, 1));     // 1 + the 1,365 nodes of the first quarter
        Assert.Equal(2731, t.NodeBit(5, 1, 0));
        Assert.Equal(4096, t.NodeBit(5, 1, 1));
        Assert.Equal(1196, t.NodeBit(3, 3, 2));
        Assert.Equal(1283, t.NodeBit(1, 12, 12));
        Assert.Equal(1284, t.LeafBit(24, 24));
        Assert.Equal(5456, t.NodeBit(1, 31, 31));
        Assert.Equal(5460, t.LeafBit(63, 63));      // the last block is the last bit
    }

    [Theory]
    [InlineData(256)]
    [InlineData(128)]
    [InlineData(512)]
    public void Every_node_has_its_own_bit_and_comes_before_its_children(int stride)
    {
        var t = new TvdtData(stride);
        var seen = new HashSet<int>();
        for (int level = 0; level <= t.TopLevel; level++)
        {
            int side = t.LeavesPerSide >> level;
            for (int y = 0; y < side; y++)
                for (int x = 0; x < side; x++)
                {
                    int bit = t.NodeBit(level, x, y);
                    Assert.True(seen.Add(bit), $"bit {bit} used twice");
                    if (level > 0)
                        foreach (var (dx, dy) in new[] { (0, 0), (0, 1), (1, 0), (1, 1) })
                            Assert.True(t.NodeBit(level - 1, 2 * x + dx, 2 * y + dy) > bit);
                }
        }
        Assert.Equal(TvdtData.SubtreeSize(t.TopLevel), seen.Count);
        Assert.Equal(0, seen.Min());
        Assert.Equal(seen.Count - 1, seen.Max());   // no gaps: the game counts the nodes one by one
        Assert.True(t.ByteCount * 8 >= seen.Count);
        Assert.Equal(0, t.ByteCount % 4);           // the game reads whole 32-bit words
    }

    [Fact]
    public void A_smaller_grid_has_a_smaller_tree_and_a_bigger_grid_a_longer_field()
    {
        var small = new TvdtData(128);
        Assert.Equal(32, small.LeavesPerSide);
        Assert.Equal(5, small.TopLevel);
        Assert.Equal(0, small.NodeBit(5, 0, 0));
        Assert.Equal(5, small.LeafBit(0, 0));       // one level less above the first block than in a 256-cell grid
        Assert.Equal(1364, small.LeafBit(31, 31));
        Assert.Equal(684, small.ByteCount);         // never shorter than the game's own
        Assert.Equal(2732, new TvdtData(512).ByteCount);   // 21,845 nodes, in whole words
        Assert.Throws<ArgumentOutOfRangeException>(() => new TvdtData(100));
    }

    [Fact]
    public void A_real_cell_decodes_and_encodes_unchanged()
    {
        var bytes = Convert.FromHexString(Golden);
        var tvdt = new TvdtData(bytes);
        Assert.Equal(185, tvdt.VisibleLeaves());
        Assert.True(tvdt.GetLeaf(24, 24));       // the observer's own block: cell (0, 0) + 96 = 96 / 4
        Assert.Equal(bytes, tvdt.ToBytes());

        var rebuilt = new TvdtData();
        for (int level = 0; level <= tvdt.TopLevel; level++)
        {
            int side = tvdt.LeavesPerSide >> level;
            for (int y = 0; y < side; y++)
                for (int x = 0; x < side; x++)
                    rebuilt.SetNode(level, x, y, tvdt.GetNode(level, x, y));
        }
        Assert.Equal(bytes, rebuilt.ToBytes());
    }

    [Fact]
    public void In_a_real_cell_every_visible_node_has_a_visible_parent()
    {
        // The game drops everything below a node it reads as hidden, so xLODGen's data only makes sense in the right order: read any other
        // way, visible blocks sit under hidden quads (in children-first order with 6 leading bits, 3 in 10 of the set nodes do).
        var tvdt = new TvdtData(Convert.FromHexString(Golden));
        Assert.True(tvdt.GetNode(tvdt.TopLevel, 0, 0));
        int set = 0;
        for (int level = 0; level < tvdt.TopLevel; level++)
        {
            int side = tvdt.LeavesPerSide >> level;
            for (int y = 0; y < side; y++)
                for (int x = 0; x < side; x++)
                {
                    if (!tvdt.GetNode(level, x, y)) continue;
                    set++;
                    Assert.True(tvdt.GetNode(level + 1, x >> 1, y >> 1), $"level {level} node ({x},{y}) is visible under a hidden parent");
                }
        }
        Assert.True(set > 185);
    }

    [Fact]
    public void Wrong_sizes_and_positions_are_refused()
    {
        Assert.Throws<ArgumentException>(() => new TvdtData(new byte[683]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TvdtData().LeafBit(64, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TvdtData().NodeBit(7, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TvdtData(128).LeafBit(32, 0));
    }
}
