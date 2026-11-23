using PenguLOD.Core.Paths;

namespace PenguLOD.Plugins;

/// <summary>What one stage adds to a volume: <see cref="During"/> while it builds, <see cref="After"/> once it has published.</summary>
public sealed record SpaceNeed(string Volume, long During, long After);

/// <summary>Free space on the volume that holds a path, and the space a run of several stages needs.</summary>
public static class DiskSpace
{
    public sealed record Volume(string Name, long Free);

    /// <summary>Stands in for the operating system's answer on the calling thread (tests).</summary>
    [ThreadStatic] internal static Func<string, Volume?>? Override;

    /// <summary>The volume holding <paramref name="path"/>, which need not exist yet. Null when it cannot be told.</summary>
    public static Volume? Of(string path)
    {
        if (Override is { } stand) return stand(path);
        try
        {
            string full = HostPath.Canonical(path);
            var drive = DriveInfo.GetDrives().Where(d => HostPath.IsWithin(full, d.Name)).OrderByDescending(d => d.Name.Length).FirstOrDefault();
            return drive is null ? null : new(drive.Name, drive.AvailableFreeSpace);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>
    /// The most each volume holds beyond what it holds now, when <paramref name="stages"/> run one after another. A stage's
    /// needs are grouped because they are held at the same time: a build beside its output, and what it keeps in the cache.
    /// </summary>
    public static IReadOnlyDictionary<string, long> Peak(IEnumerable<IReadOnlyList<SpaceNeed>> stages)
    {
        var kept = new Dictionary<string, long>(StringComparer.Ordinal); var peak = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var stage in stages)
        {
            foreach (var volume in stage.GroupBy(n => n.Volume, StringComparer.Ordinal))
                peak[volume.Key] = Math.Max(peak.GetValueOrDefault(volume.Key), kept.GetValueOrDefault(volume.Key) + volume.Sum(n => n.During));
            foreach (var need in stage) kept[need.Volume] = kept.GetValueOrDefault(need.Volume) + need.After;
        }
        return peak;
    }

    public static string Text(long bytes) => FormattableString.Invariant($"{bytes / 1073741824.0:N1} GiB");

    /// <summary>Total size of the files below <paramref name="folder"/>; links are not followed.</summary>
    public static long SizeOf(string folder, CancellationToken ct = default)
    {
        long total = 0;
        if (!Directory.Exists(folder) || new DirectoryInfo(folder).LinkTarget is not null) return 0;
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }))
        { ct.ThrowIfCancellationRequested(); total += file.Length; }
        return total;
    }
}
