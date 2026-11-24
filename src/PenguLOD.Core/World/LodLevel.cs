namespace PenguLOD.Core.World;

/// <summary>
/// Object LOD levels. The value is the block size in cells.
/// The STAT record's MNAM holds four mesh slots (Level0..Level3) that map to these in order.
/// </summary>
public enum LodLevel
{
    Lod4 = 4,
    Lod8 = 8,
    Lod16 = 16,
    Lod32 = 32,
}

public static class LodLevels
{
    public static readonly IReadOnlyList<LodLevel> All = [LodLevel.Lod4, LodLevel.Lod8, LodLevel.Lod16, LodLevel.Lod32];

    /// <summary>The levels a request selects. Null and empty both mean every level.</summary>
    public static IReadOnlyCollection<LodLevel> Selected(IReadOnlyCollection<LodLevel>? requested) =>
        requested is { Count: > 0 } ? requested : All;

    /// <summary>MNAM slot index (0..3) for a level.</summary>
    public static int SlotIndex(this LodLevel level) => level switch
    {
        LodLevel.Lod4 => 0,
        LodLevel.Lod8 => 1,
        LodLevel.Lod16 => 2,
        LodLevel.Lod32 => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    public static bool TryParse(int cells, out LodLevel level)
    {
        level = (LodLevel)cells;
        return cells is 4 or 8 or 16 or 32;
    }
}

/// <summary>Per-LOD-level configuration values (LOD4, LOD8, LOD16, LOD32).</summary>
public sealed record TerrainLevelValues<T>(T Lod4, T Lod8, T Lod16, T Lod32)
{
    public T For(LodLevel level) => level switch
    {
        LodLevel.Lod4 => Lod4,
        LodLevel.Lod8 => Lod8,
        LodLevel.Lod16 => Lod16,
        LodLevel.Lod32 => Lod32,
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    public IEnumerable<T> All()
    {
        yield return Lod4;
        yield return Lod8;
        yield return Lod16;
        yield return Lod32;
    }
}
