using System.Text.RegularExpressions;
using PenguLOD.Core.Paths;
using PenguLOD.Core.Pipeline;

namespace PenguLOD.Plugins;

/// <summary>What a folder PenguLOD keeps on disk is.</summary>
public static class StorageKinds
{
    public const string Output = "Output";
    public const string Previous = "Previous output";
    public const string Build = "Unfinished build";
    public const string Unpublished = "Finished build that was not published";
    public const string OtherPrevious = "Previous output of another folder";
    public const string OtherBuild = "Unfinished build of another folder";
    public const string Checkpoints = "Terrain tile checkpoints";
}

/// <summary>One folder PenguLOD keeps on disk: an output, an earlier copy of one, a build, or checkpoints in the cache.</summary>
/// <param name="Generated">When the output in the folder was generated, from its receipt.</param>
/// <param name="Replaced">For a previous output, when a newer one took its place.</param>
/// <param name="Output">The output folder this belongs to, when it is known. Its lock is held while the folder is removed.</param>
/// <param name="LockFile">For a folder with no known output, the lock file of the job it belongs to.</param>
public sealed record StorageItem(string Path, string Kind, long Bytes, DateTime? Generated = null, DateTime? Replaced = null,
    string? Version = null, string? Output = null, string? LockFile = null)
{
    public bool Removable => Kind != StorageKinds.Output;
    public bool Restorable => Kind == StorageKinds.Previous;
}

/// <summary>Lists what the stage outputs and the cache hold on disk, and removes the parts that are not the outputs.</summary>
public static partial class OutputStorage
{
    [GeneratedRegex(@"^(?<output>.+)\.previous-\d{8}-\d{6}-\d{3}-[0-9a-f]{6}$")] private static partial Regex PreviousName();
    [GeneratedRegex(@"^\.pengulod-build-(?<output>[0-9A-F]{12})-[0-9a-f]{32}$")] private static partial Regex WorkspaceName();
    [GeneratedRegex(@"^\.pengulod-(grass-)?build-[0-9a-f]{32}$")] private static partial Regex LegacyWorkspaceName();

    /// <param name="outputs">The stage output folders. Their parent folders are searched for what belongs to other outputs.</param>
    /// <param name="cache">The cache folder whose saved jobs are measured, or null.</param>
    public static IReadOnlyList<StorageItem> Survey(IEnumerable<string> outputs, string? cache, CancellationToken ct = default)
    {
        var paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var named = outputs.Select(HostPath.Canonical).Select(Path.TrimEndingDirectorySeparator).Distinct(paths).ToArray();
        var items = new List<StorageItem>(); var listed = new HashSet<string>(paths);
        void Add(string dir, string kind, DateTime? replaced = null, string? output = null, string? lockFile = null)
        {
            if (new DirectoryInfo(dir).LinkTarget is not null || !listed.Add(dir)) return;
            var (generated, version) = Receipt(kind is StorageKinds.Build or StorageKinds.Unpublished or StorageKinds.OtherBuild ? Path.Combine(dir, "output") : dir);
            items.Add(new(dir, kind, OutputCleanup.Size(dir, ct), generated, replaced, version, output, lockFile));
        }
        foreach (string output in named)
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(output)) Add(output, StorageKinds.Output, output: output);
            foreach (string copy in OutputRetention.PreviousCopies(output)) Add(copy, StorageKinds.Previous, OutputRetention.ReplacedAt(copy), output);
            if (Path.GetDirectoryName(output) is { } parent && Directory.Exists(parent))
                foreach (string build in Directory.EnumerateDirectories(parent, StagedOutput.WorkspacePrefix(output) + "*"))
                    Add(build, File.Exists(Path.Combine(build, StagedOutput.UnpublishedMarker)) ? StorageKinds.Unpublished : StorageKinds.Build, output: output);
        }
        // What other outputs left in the same folders: an output folder that was renamed, or one that is not named here.
        foreach (string parent in named.Select(Path.GetDirectoryName).OfType<string>().Distinct(paths).Where(Directory.Exists))
            foreach (string dir in Directory.EnumerateDirectories(parent))
            {
                ct.ThrowIfCancellationRequested();
                string name = Path.GetFileName(dir);
                if (PreviousName().Match(name) is { Success: true } previous)
                    Add(dir, StorageKinds.OtherPrevious, OutputRetention.ReplacedAt(dir), output: Path.Combine(parent, previous.Groups["output"].Value));
                else if (WorkspaceName().Match(name) is { Success: true } build)
                    Add(dir, StorageKinds.OtherBuild, lockFile: Directory.EnumerateFiles(parent, ".pengulod-output-" + build.Groups["output"].Value + "*.lock").FirstOrDefault());
                else if (LegacyWorkspaceName().IsMatch(name)) Add(dir, StorageKinds.OtherBuild);
            }
        if (cache is not null && Directory.Exists(Path.Combine(cache, "jobs")))
            foreach (string job in Directory.EnumerateDirectories(Path.Combine(cache, "jobs")))
            {
                ct.ThrowIfCancellationRequested();
                string tiles = Path.Combine(job, "terrain-tiles");
                if (Directory.Exists(tiles) && OutputCleanup.Size(tiles, ct) is > 0 and var bytes && listed.Add(tiles) && new DirectoryInfo(tiles).LinkTarget is null)
                    items.Add(new(tiles, StorageKinds.Checkpoints, bytes, Directory.GetLastWriteTime(tiles), LockFile: Path.Combine(job, "job.lock")));
            }
        return items;

        static (DateTime? Generated, string? Version) Receipt(string folder)
        {
            try
            {
                if (!Directory.Exists(folder) || !OutputSetReceipt.Exists(folder)) return (null, null);
                var stages = OutputSetReceipt.Read(folder).Stages;
                return (stages.Max(s => s.Completed).LocalDateTime, string.Join(", ", stages.Select(s => s.Version).Distinct()));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException) { return (null, null); }
        }
    }

    /// <summary>
    /// Removes the folders of <paramref name="items"/>, each under the lock of the job it belongs to. An output is never
    /// removed, and a folder whose job is running is left. Returns the number removed.
    /// </summary>
    public static int Remove(IReadOnlyList<StorageItem> items, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        int removed = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var dir = new DirectoryInfo(item.Path);
                if (!item.Removable || !dir.Exists || dir.LinkTarget is not null || !StillThatKind(item))
                { progress?.Report("Left as it is: " + item.Path); continue; }
                using var guard = item.Output is not null ? OutputLock.Acquire(item.Output)
                    : item.LockFile is not null && File.Exists(item.LockFile) && new FileInfo(item.LockFile).LinkTarget is null
                        ? new FileStream(item.LockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
                // A build with no lock to ask may belong to a job that is running, unless it has not been written to for a day.
                if (guard is null && item.Kind == StorageKinds.OtherBuild && DateTime.UtcNow - dir.LastWriteTimeUtc < OutputCleanup.LegacyMinimumAge)
                { progress?.Report("Left, because a job may be using it: " + item.Path); continue; }
                dir.Delete(recursive: true); removed++;
                progress?.Report($"Removed {item.Path} ({item.Bytes / 1048576.0:N0} MiB)");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { progress?.Report("Could not remove " + item.Path + ": " + e.Message); }
        }
        return removed;
    }

    /// <summary>The folder's name still says what the survey said, so a list that has gone stale does not remove something else.</summary>
    private static bool StillThatKind(StorageItem item)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(item.Path));
        return item.Kind switch
        {
            StorageKinds.Previous or StorageKinds.OtherPrevious => PreviousName().IsMatch(name),
            StorageKinds.Build or StorageKinds.Unpublished or StorageKinds.OtherBuild => WorkspaceName().IsMatch(name) || LegacyWorkspaceName().IsMatch(name),
            StorageKinds.Checkpoints => name == "terrain-tiles",
            _ => false
        };
    }
}
