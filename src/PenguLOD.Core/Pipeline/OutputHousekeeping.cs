using System.Collections.Immutable;
using PenguLOD.Core.Paths;

namespace PenguLOD.Core.Pipeline;

/// <summary>Stage identifiers used in journals, receipts and plans.</summary>
public static class StageIds
{
    public const string Grass = "grass";
    public const string Textures = "textures";
    public const string Terrain = "terrain";
    public const string Lod = "lod";
    public static readonly IReadOnlyList<string> All = [Grass, Textures, Terrain, Lod];
    public static bool IsKnown(string? id) => id is Grass or Textures or Terrain or Lod;
}

/// <summary>
/// Files in an output folder that belong to the user or the mod manager, not to a generation: they are never hashed,
/// verified, replaced or deleted by a stage. A new generation keeps the installed copy.
/// </summary>
public static class UserOwnedFiles
{
    /// <summary>Paths relative to the output folder, '/'-separated.</summary>
    public static readonly IReadOnlyList<string> Paths = ["meta.ini", "SKSE/Plugins/PenguLOD.ini"];

    public static bool Contains(string relative)
    {
        string normalized = relative.Replace('\\', '/').TrimStart('/');
        foreach (string path in Paths)
            if (path.Equals(normalized, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The user-owned files present in <paramref name="folder"/>, with their on-disk relative spelling.</summary>
    public static IEnumerable<string> Existing(string folder)
    {
        if (!Directory.Exists(folder)) yield break;
        foreach (string path in Paths)
            if (PenguLOD.Core.World.GamePath.ResolveAllExisting(folder, path).LastOrDefault(File.Exists) is { } found
                && new FileInfo(found).LinkTarget is null)
                yield return Path.GetRelativePath(folder, found).Replace('\\', '/');
    }
}

/// <summary>
/// Exclusive lock on an output location, shared by every PenguLOD process. Code running inside a job that already holds
/// the lock (a stage within its pipeline) joins it; a separate job, even in the same process, is refused.
/// </summary>
public static class OutputLock
{
    private static readonly AsyncLocal<ImmutableHashSet<string>?> Flow = new();

    public static string PathFor(string output)
    {
        string canonical = HostPath.Canonical(output);
        return Path.Combine(Path.GetDirectoryName(canonical)!,
            ".pengulod-output-" + OutputSnapshot.HashText(HostPath.ComparisonKey(canonical))[..24] + ".lock");
    }

    /// <summary>True when this execution flow already holds the lock for <paramref name="output"/>.</summary>
    public static bool HeldHere(string output) => Flow.Value?.Contains(PathFor(output)) == true;

    public static IDisposable Acquire(string output)
    {
        string file = PathFor(output);
        if (Flow.Value?.Contains(file) == true) return new Release(null, null);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (new FileInfo(file).LinkTarget is not null) throw new IOException("Output lock must not be a symbolic link: " + file);
        FileStream stream;
        try { stream = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) { throw new IOException("Another generation job is using this output or checkpoint: " + file, e); }
        Flow.Value = (Flow.Value ?? ImmutableHashSet<string>.Empty).Add(file);
        return new Release(file, stream);
    }

    private sealed class Release(string? file, FileStream? stream) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0 || file is null) return;
            if (Flow.Value is { } held) Flow.Value = held.Remove(file);
            stream!.Dispose();
        }
    }
}

/// <summary>How many earlier copies of an output a publication keeps beside it (".previous-*"), and for how long.</summary>
public static class OutputRetention
{
    public const int Default = 1;
    public const int Maximum = 100;
    /// <summary>Days an earlier copy is kept after it was replaced. 0 keeps it until newer copies take its place.</summary>
    public const int DefaultDays = 14;
    public const int MaximumDays = 3650;
    private const string Infix = ".previous-", Stamp = "yyyyMMdd-HHmmss-fff";
    private static readonly AsyncLocal<(int Keep, int Days)?> Current = new();
    public static int KeepPrevious => Current.Value?.Keep ?? Default;
    public static int KeepDays => Current.Value?.Days ?? DefaultDays;

    public static IDisposable Enter(int keepPrevious, int keepDays = DefaultDays)
    {
        if (keepPrevious is < 0 or > Maximum) throw new ArgumentOutOfRangeException(nameof(keepPrevious), $"Keep between 0 and {Maximum} previous outputs.");
        if (keepDays is < 0 or > MaximumDays) throw new ArgumentOutOfRangeException(nameof(keepDays), $"Keep previous outputs between 0 and {MaximumDays} days.");
        var previous = Current.Value; Current.Value = (keepPrevious, keepDays);
        return new Scope(() => Current.Value = previous);
    }

    /// <summary>The folder name an output replaced at <paramref name="replaced"/> (local time) is kept under.</summary>
    public static string PreviousName(string output, DateTime replaced) =>
        Path.TrimEndingDirectorySeparator(output) + Infix + replaced.ToString(Stamp, System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];

    /// <summary>When an earlier copy was replaced (local time), read from its folder name. Null when the name does not say.</summary>
    public static DateTime? ReplacedAt(string previousCopy)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(previousCopy));
        int at = name.LastIndexOf(Infix, StringComparison.Ordinal);
        if (at < 0 || name.Length < at + Infix.Length + Stamp.Length) return null;
        return DateTime.TryParseExact(name.AsSpan(at + Infix.Length, Stamp.Length), Stamp, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal, out var replaced) ? replaced : null;
    }

    /// <summary>Earlier copies of <paramref name="output"/>, newest first. Their names sort by publication time.</summary>
    public static IReadOnlyList<string> PreviousCopies(string output)
    {
        string full = Path.TrimEndingDirectorySeparator(HostPath.Canonical(output));
        string parent = Path.GetDirectoryName(full)!, prefix = Path.GetFileName(full) + Infix;
        if (!Directory.Exists(parent)) return [];
        return Directory.EnumerateDirectories(parent)
            .Where(d => Path.GetFileName(d).StartsWith(prefix, StringComparison.Ordinal) && new DirectoryInfo(d).LinkTarget is null)
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Earlier copies replaced more than <paramref name="days"/> days ago. None when <paramref name="days"/> is 0, and none
    /// while the output itself is missing: a copy may then be all that is left of it.
    /// </summary>
    public static IReadOnlyList<string> Expired(string output, int days, DateTime? now = null)
    {
        if (days <= 0 || !Directory.Exists(output)) return [];
        DateTime limit = (now ?? DateTime.Now).AddDays(-days);
        return PreviousCopies(output).Where(copy => ReplacedAt(copy) < limit).ToArray();
    }

    /// <summary>
    /// Deletes earlier copies beyond the newest <paramref name="keep"/>, and those replaced more than <paramref name="days"/>
    /// days ago (0: no age limit). Returns the folders removed.
    /// </summary>
    public static IReadOnlyList<string> Prune(string output, int keep, IProgress<string>? progress = null, int days = 0, DateTime? now = null)
    {
        int extra = Remove(PreviousCopies(output).Skip(Math.Max(0, keep)), progress, out var removed);
        if (extra > 0) progress?.Report($"Removed {extra} older previous output(s); keeping {keep}.");
        removed.AddRange(Expire(output, days, progress, now));
        return removed;
    }

    /// <summary>Deletes the earlier copies <see cref="Expired"/> names. Returns the folders removed.</summary>
    public static IReadOnlyList<string> Expire(string output, int days, IProgress<string>? progress = null, DateTime? now = null)
    {
        int old = Remove(Expired(output, days, now), progress, out var removed);
        if (old > 0) progress?.Report($"Removed {old} previous output(s) replaced more than {days} days ago: " + string.Join(", ", removed.Select(Path.GetFileName)));
        return removed;
    }

    private static int Remove(IEnumerable<string> copies, IProgress<string>? progress, out List<string> removed)
    {
        removed = [];
        foreach (string old in copies)
        {
            try { Directory.Delete(old, recursive: true); removed.Add(old); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { progress?.Report("Could not remove previous output " + old + ": " + e.Message); }
        }
        return removed.Count;
    }

    private sealed class Scope(Action end) : IDisposable
    { private Action? _end = end; public void Dispose() => Interlocked.Exchange(ref _end, null)?.Invoke(); }
}

/// <summary>Keeps the newest entries of a folder of timestamped run folders.</summary>
public static class FolderRetention
{
    /// <summary>Deletes all but the <paramref name="keep"/> most recently written subfolders of <paramref name="parent"/>.</summary>
    public static int Prune(string parent, int keep)
    {
        if (!Directory.Exists(parent)) return 0;
        int removed = 0;
        foreach (var dir in new DirectoryInfo(parent).EnumerateDirectories().Where(d => d.LinkTarget is null)
            .OrderByDescending(d => d.LastWriteTimeUtc).ThenByDescending(d => d.Name, StringComparer.Ordinal).Skip(Math.Max(0, keep)))
        {
            try { dir.Delete(recursive: true); removed++; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }
}
