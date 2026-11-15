namespace PenguLOD.Core.Pipeline;

/// <summary>
/// One spelling for every output path. The game and Windows read "meshes\Terrain" and "meshes\terrain" as one folder; on a
/// case-sensitive file system they are two, and a mod manager or Wine then shows the game only one of them, so the files in the
/// other are silently missing in game. Generated paths take their spelling from many places (constants, worldspace names, texture
/// paths in meshes), so the output is checked as a whole instead of trusting every writer.
/// </summary>
public static class OutputCase
{
    /// <summary>
    /// Entries of one folder whose names differ only in case, as groups of paths relative to <paramref name="root"/> ('/'-separated).
    /// Empty on a file system that does not tell such names apart.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> Conflicts(string root)
    {
        var found = new List<IReadOnlyList<string>>();
        if (!Directory.Exists(root)) return found;
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var dir))
        {
            var entries = Directory.EnumerateFileSystemEntries(dir).ToList();
            foreach (var group in entries.GroupBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                found.Add(group.Select(p => Relative(root, p)).Order(StringComparer.Ordinal).ToList());
            foreach (var sub in entries.Where(IsRealDirectory)) pending.Push(sub);
        }
        found.Sort((a, b) => string.CompareOrdinal(a[0], b[0]));
        return found;
    }

    /// <summary>
    /// Merges entries whose names differ only in case into one, anywhere below <paramref name="root"/>: folders are combined, and of
    /// two files the one written last is kept. The all-lower-case spelling wins when there is one, otherwise the first in ordinal
    /// order. Returns the relative paths that were merged away. Links are left alone.
    /// </summary>
    public static IReadOnlyList<string> Unify(string root)
    {
        var merged = new List<string>();
        if (Directory.Exists(root) && IsRealDirectory(root)) UnifyFolder(root, root, merged);
        return merged;
    }

    private static void UnifyFolder(string root, string dir, List<string> merged)
    {
        var groups = Directory.EnumerateFileSystemEntries(dir)
            .GroupBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
            .Select(g => g.OrderBy(p => Path.GetFileName(p) == Path.GetFileName(p).ToLowerInvariant() ? 0 : 1).ThenBy(p => p, StringComparer.Ordinal).ToList())
            .ToList();
        foreach (var group in groups)
            foreach (var other in group.Skip(1))
            {
                string was = Relative(root, other);
                MergeInto(other, group[0]);
                merged.Add(was);
            }
        foreach (var sub in Directory.EnumerateDirectories(dir).Where(IsRealDirectory).ToList()) UnifyFolder(root, sub, merged);
    }

    private static void MergeInto(string source, string target)
    {
        bool sourceIsFolder = Directory.Exists(source), targetIsFolder = Directory.Exists(target);
        if (IsLink(source) || IsLink(target) || sourceIsFolder != targetIsFolder)
            throw new IOException($"The output has a file and a folder, or a link, under names that differ only in case: '{source}' and '{target}'.");
        if (!sourceIsFolder)
        {
            if (File.GetLastWriteTimeUtc(source) > File.GetLastWriteTimeUtc(target)) File.Move(source, target, overwrite: true);
            else File.Delete(source);
            return;
        }
        foreach (var child in Directory.EnumerateFileSystemEntries(source).ToList())
        {
            string name = Path.GetFileName(child);
            string? existing = Directory.EnumerateFileSystemEntries(target)
                .Where(p => string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault();
            if (existing is not null) MergeInto(child, existing);
            else if (Directory.Exists(child)) Directory.Move(child, Path.Combine(target, name));
            else File.Move(child, Path.Combine(target, name));
        }
        Directory.Delete(source);
    }

    private static bool IsLink(string path)
        => (Directory.Exists(path) ? new DirectoryInfo(path) : (FileSystemInfo)new FileInfo(path)).LinkTarget is not null;

    private static bool IsRealDirectory(string path) => Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is null;

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}
