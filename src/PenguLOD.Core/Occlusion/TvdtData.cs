using System.Numerics;

namespace PenguLOD.Core.Occlusion;

/// <summary>
/// The TVDT field of an exterior CELL: one bit per node of the worldspace's LOD quadtree, numbered the way the game numbers its nodes.
/// The root covers the whole grid of the worldspace's .lod file (<c>Stride</c> cells per side, from its south-west cell). The game gives
/// each node the next number and then builds its four children, so a node comes before everything below it (pre-order): bit 0 is the
/// root, bit 1 its first child, and so on down to the first 4-cell block. Children come in Z order with the Y bit lowest:
/// child = (xBit &lt;&lt; 1) | yBit. Bits are numbered from the least significant bit of byte 0. Level 0 = the 4-cell blocks, 1 = 8 cells, ...
/// <see cref="TopLevel"/> = the root. Tamriel (stride 256) has 64 x 64 blocks, 5,461 nodes and 684 bytes.
/// <para>
/// What the game does with it (read from the node update of SkyrimSE 1.6.1170, and the same in the game's own data and xLODGen's): a set
/// bit means the node can be seen from this cell. A node of 4, 8, 32 or more cells whose bit is clear in all nine cells around the camera
/// loses its object LOD and so does everything below it; 16-cell nodes are skipped by that test. A block that is loaded is drawn only
/// while its own bit is set in the camera's cell. A cell without the field hides nothing. So a node must be set whenever anything below
/// it is, all the way up to the root.
/// </para>
/// </summary>
public sealed class TvdtData
{
    /// <summary>Grid size the game's own data uses (Tamriel and most worldspaces).</summary>
    public const int DefaultStride = 256;
    /// <summary>The field is never written shorter than this: the size for a 256-cell grid, which is what the game's own cells carry.</summary>
    public const int MinByteCount = 684;

    private readonly byte[] _bytes;

    /// <summary>4-cell blocks per side of the grid.</summary>
    public int LeavesPerSide { get; }
    /// <summary>Level of the root (0 = the 4-cell blocks).</summary>
    public int TopLevel { get; }
    public int ByteCount => _bytes.Length;

    public TvdtData(int stride = DefaultStride)
    {
        TopLevel = TopLevelOf(stride);
        LeavesPerSide = 1 << TopLevel;
        _bytes = new byte[ByteCountOf(stride)];
    }

    public TvdtData(ReadOnlySpan<byte> bytes, int stride = DefaultStride) : this(stride)
    {
        if (bytes.Length != _bytes.Length) throw new ArgumentException($"A TVDT for a grid of {stride} cells is {_bytes.Length} bytes, got {bytes.Length}.", nameof(bytes));
        bytes.CopyTo(_bytes);
    }

    /// <summary>Level of the root for a grid of <paramref name="stride"/> cells per side (a power of two, 4 or more).</summary>
    public static int TopLevelOf(int stride)
    {
        if (stride < 4 || !BitOperations.IsPow2(stride)) throw new ArgumentOutOfRangeException(nameof(stride), $"A LOD grid is a power of two of at least 4 cells, got {stride}.");
        return BitOperations.Log2((uint)stride) - 2;
    }

    /// <summary>Nodes in a full quadtree of this height (0 = one block): (4^(h+1) - 1) / 3.</summary>
    public static int SubtreeSize(int height) => (int)(((1L << (2 * (height + 1))) - 1) / 3);

    /// <summary>Bytes of the field for a grid of <paramref name="stride"/> cells: whole 32-bit words, as the game reads it.</summary>
    public static int ByteCountOf(int stride) => Math.Max(MinByteCount, (SubtreeSize(TopLevelOf(stride)) + 31) / 32 * 4);

    public byte[] ToBytes() => (byte[])_bytes.Clone();

    public bool GetBit(int index) => (_bytes[index >> 3] >> (index & 7) & 1) != 0;

    public void SetBit(int index, bool value)
    {
        if (value) _bytes[index >> 3] |= (byte)(1 << (index & 7));
        else _bytes[index >> 3] &= (byte)~(1 << (index & 7));
    }

    /// <summary>
    /// Bit number of the node at <paramref name="level"/> (0 = a 4-cell block) and grid position (x, y) at that level, in a tree whose
    /// root is at <paramref name="topLevel"/>: one for every node passed on the way down, plus the whole subtrees of the earlier children.
    /// </summary>
    public static int NodeBit(int level, int x, int y, int topLevel)
    {
        if (level < 0 || level > topLevel) throw new ArgumentOutOfRangeException(nameof(level));
        int side = 1 << (topLevel - level);
        if ((uint)x >= (uint)side || (uint)y >= (uint)side) throw new ArgumentOutOfRangeException($"({x},{y}) is outside the {side} x {side} grid of level {level}.");
        int bx = x << level, by = y << level, index = 0;
        for (int h = topLevel; h > level; h--)
        {
            int k = h - 1;
            int child = (((bx >> k) & 1) << 1) | ((by >> k) & 1);
            index += 1 + child * SubtreeSize(h - 1);
        }
        return index;
    }

    public int NodeBit(int level, int x, int y) => NodeBit(level, x, y, TopLevel);

    public int LeafBit(int blockX, int blockY) => NodeBit(0, blockX, blockY, TopLevel);

    public bool GetNode(int level, int x, int y) => GetBit(NodeBit(level, x, y));

    public void SetNode(int level, int x, int y, bool value) => SetBit(NodeBit(level, x, y), value);

    public bool GetLeaf(int blockX, int blockY) => GetBit(LeafBit(blockX, blockY));

    public void SetLeaf(int blockX, int blockY, bool value) => SetBit(LeafBit(blockX, blockY), value);

    /// <summary>Number of visible 4-cell blocks.</summary>
    public int VisibleLeaves()
    {
        int n = 0;
        for (int y = 0; y < LeavesPerSide; y++) for (int x = 0; x < LeavesPerSide; x++) if (GetLeaf(x, y)) n++;
        return n;
    }
}
