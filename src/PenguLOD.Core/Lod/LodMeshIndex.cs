using System.Text.RegularExpressions;

namespace PenguLOD.Core.Lod;

/// <summary>
/// Finds LOD meshes for a full model by file name, using the convention DynDOLOD and the
/// LOD resource packs (DynDOLOD Resources, FOLIP, Lod Model Library…) are built around:
/// <code>
/// house.nif  →  house_lod_0.nif … house_lod_3.nif   (or house_lod.nif for levels 0-2, or level 0 next to numbered files; house_lod_4.nif is level 3 only)
/// </code>
/// anywhere under <c>meshes\</c>. When the same file name exists in several folders the later
/// folder wins: <c>meshes</c> &lt; <c>meshes\dlc01\lod</c> &lt; <c>meshes\dlc02\lod</c> &lt; <c>meshes\lod</c> &lt; <c>meshes\dyndolod</c>.
/// Missing lower levels fall back to the next higher numbered level that exists; name_lod_4.nif fills none.
/// Reference: https://dyndolod.info/Mod-Authors
/// </summary>
public sealed class LodMeshIndex
{
    // name_lod.nif / name_lod_2.nif  (name may itself contain "_[CRC32]")
    private static readonly Regex LodName = new(@"^(?<base>.+?)_lod(?:_(?<lvl>[0-4]))?\.nif$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly Dictionary<string, Entry> _byBase = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Entry
    {
        // name_lod_0.nif … name_lod_3.nif; name_lod_4.nif sits in slot 3 with Lod4 set.
        public readonly string?[] Paths = new string?[4];
        public readonly int[] Ranks = [-1, -1, -1, -1];
        public bool Lod4;
        // name_lod.nif
        public string? Generic;
        public int GenericRank = -1;

        /// <summary>
        /// The mesh per level as DynDOLOD assigns them: name_lod.nif alone covers levels 0, 1 and 2; next to numbered files it only
        /// fills a missing level 0. With <paramref name="fill"/>, a missing level uses the next higher numbered file, except that
        /// name_lod_4.nif fills no other level.
        /// </summary>
        public string?[] Levels(bool fill)
        {
            var result = (string?[])Paths.Clone();
            bool numbered = Paths[0] is not null || Paths[1] is not null || Paths[2] is not null || Paths[3] is not null && !Lod4;
            if (Generic is not null)
                for (int l = 0; l <= (numbered ? 0 : 2); l++)
                    if (GenericRank > Ranks[l] || (GenericRank == Ranks[l] && string.CompareOrdinal(Generic, Paths[l]) < 0)) result[l] = Generic;
            if (fill)
                for (int l = 2; l >= 0; l--)
                    result[l] ??= l == 2 && Lod4 ? null : result[l + 1];
            return result;
        }
    }

    public int Count => _byBase.Count;

    /// <param name="meshPaths">Data-relative paths ("meshes\..."), any case, any separators.</param>
    public static LodMeshIndex Build(IEnumerable<string> meshPaths)
    {
        var idx = new LodMeshIndex();
        foreach (var raw in meshPaths)
        {
            var path = Normalize(raw);
            if (!path.StartsWith("meshes\\", StringComparison.Ordinal)) continue;
            int slash = path.LastIndexOf('\\');
            var file = path[(slash + 1)..];
            var m = LodName.Match(file);
            if (!m.Success) continue;

            var key = m.Groups["base"].Value;
            int rank = FolderRank(path) * 2; // odd = explicit level name, beats name_lod.nif in the same folder rank
            if (!idx._byBase.TryGetValue(key, out var e)) idx._byBase[key] = e = new Entry();

            if (m.Groups["lvl"].Success)
            {
                // name_lod_4.nif is the LOD32 file (DynDOLOD: "assigned to level 3 only and not fill any of the lower levels";
                // FOLIP's road chunks use it). An explicit name_lod_3.nif in the same folder rank beats it.
                int lvl = int.Parse(m.Groups["lvl"].Value);
                if (lvl == 4) { if (Put(e, 3, path, rank)) e.Lod4 = true; }
                else if (Put(e, lvl, path, rank + 1) && lvl == 3) e.Lod4 = false;
            }
            else if (rank > e.GenericRank || (rank == e.GenericRank && string.CompareOrdinal(path, e.Generic) < 0))
            {
                e.GenericRank = rank;
                e.Generic = path;
            }
        }
        return idx;
    }

    private static bool Put(Entry e, int level, string path, int rank)
    {
        // Higher folder rank wins; within the same folder rank an explicit level beats the generic name_lod.nif
        // (the rank's low bit); equal ranks prefer the lexically smallest path for determinism.
        if (rank > e.Ranks[level] || (rank == e.Ranks[level] && string.CompareOrdinal(path, e.Paths[level]) < 0))
        {
            e.Ranks[level] = rank;
            e.Paths[level] = path;
            return true;
        }
        return false;
    }

    internal static int FolderRank(string path) =>
        path.StartsWith("meshes\\dyndolod\\", StringComparison.Ordinal) ? 4 :
        path.StartsWith("meshes\\lod\\", StringComparison.Ordinal) ? 3 :
        path.StartsWith("meshes\\dlc02\\lod\\", StringComparison.Ordinal) ? 2 :
        path.StartsWith("meshes\\dlc01\\lod\\", StringComparison.Ordinal) ? 1 : 0;

    /// <summary>
    /// LOD meshes (levels 0..3) for a full model path such as "meshes\architecture\house.nif",
    /// with missing lower levels filled from higher ones. Null if nothing matches.
    /// </summary>
    public string?[]? Find(string fullModelPath)
    {
        var path = Normalize(fullModelPath);
        int slash = path.LastIndexOf('\\');
        var name = path[(slash + 1)..];
        if (name.EndsWith(".nif", StringComparison.Ordinal)) name = name[..^4];
        if (!_byBase.TryGetValue(name, out var e)) return null;

        var result = e.Levels(fill: true);
        return result.Any(p => p is not null) ? result : null;
    }

    /// <summary>
    /// The glow LOD mesh of a full model: <c>house.nif</c> → <c>houseglow_lod_N.nif</c> (DynDOLOD's window glow meshes). The lowest N that
    /// exists decides the grid it is drawn in: 0 = near grid (LOD4), 1 = far grid (LOD8), 2 = never fades. Null if there is none.
    /// </summary>
    public (string Path, int Level)? GlowFor(string fullModelPath)
    {
        var path = Normalize(fullModelPath);
        int slash = path.LastIndexOf('\\');
        var name = path[(slash + 1)..];
        if (name.EndsWith(".nif", StringComparison.Ordinal)) name = name[..^4];
        if (!_byBase.TryGetValue(name + "glow", out var e)) return null;
        var levels = e.Levels(fill: false);
        for (int l = 0; l <= 2; l++)
            if (levels[l] is { } p) return (p, l);
        return null;
    }

    public static string Normalize(string p)
    {
        var s = p.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
        if (s.StartsWith("data\\", StringComparison.Ordinal)) s = s[5..];
        if (!s.StartsWith("meshes\\", StringComparison.Ordinal)) s = "meshes\\" + s;
        return s;
    }
}
