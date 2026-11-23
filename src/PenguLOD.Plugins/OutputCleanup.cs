using PenguLOD.Core.Paths;
using PenguLOD.Core.Pipeline;

namespace PenguLOD.Plugins;

public sealed record CleanupItem(string Path, string Reason, long Bytes);

/// <summary>Finds and removes previous copies and unfinished build folders that publication leaves beside an output.</summary>
public static class OutputCleanup
{
    /// <summary>Unattributed build folders of older versions are only removed once they are this old.</summary>
    public static readonly TimeSpan LegacyMinimumAge = TimeSpan.FromHours(24);

    /// <param name="previousDays">Previous copies replaced more than this many days ago are listed too. 0: no age limit.</param>
    public static IReadOnlyList<CleanupItem> Find(string output, int keepPrevious, CancellationToken ct = default, int previousDays = 0, DateTime? now = null)
    {
        if (keepPrevious is < 0 or > OutputRetention.Maximum) throw new ArgumentOutOfRangeException(nameof(keepPrevious));
        if (previousDays is < 0 or > OutputRetention.MaximumDays) throw new ArgumentOutOfRangeException(nameof(previousDays));
        string full = HostPath.Canonical(output), parent = Path.GetDirectoryName(full)!;
        var items = new List<CleanupItem>();
        void Add(string dir, string reason) { if (new DirectoryInfo(dir).LinkTarget is null) items.Add(new(dir, reason, Size(dir, ct))); }
        var extra = OutputRetention.PreviousCopies(full).Skip(keepPrevious).ToArray();
        foreach (string old in extra) Add(old, "previous output");
        foreach (string old in OutputRetention.Expired(full, previousDays, now).Except(extra)) Add(old, $"previous output replaced more than {previousDays} days ago");
        if (Directory.Exists(parent))
        {
            foreach (string dir in Directory.EnumerateDirectories(parent, StagedOutput.WorkspacePrefix(full) + "*")) Add(dir, "unfinished build");
            // Earlier versions named workspaces without their output; they cannot be attributed, so only stale ones are listed.
            foreach (string dir in Directory.EnumerateDirectories(parent))
            {
                string name = Path.GetFileName(dir);
                bool legacy = (name.StartsWith(".pengulod-build-", StringComparison.Ordinal) && name.Length == ".pengulod-build-".Length + 32)
                    || (name.StartsWith(".pengulod-grass-build-", StringComparison.Ordinal) && name.Length == ".pengulod-grass-build-".Length + 32);
                if (legacy && DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) >= LegacyMinimumAge) Add(dir, "unfinished build of an earlier version");
            }
        }
        if (Directory.Exists(full))
            foreach (string dir in Directory.EnumerateDirectories(full, ".texture-build-*")) Add(dir, "unfinished texture build of an earlier version");
        return items;
    }

    /// <summary>Removes <paramref name="items"/> found for <paramref name="output"/> while holding its output lock.</summary>
    public static int Apply(string output, IReadOnlyList<CleanupItem> items, IProgress<string>? progress = null)
    {
        using var guard = OutputLock.Acquire(output);
        int removed = 0;
        foreach (var item in items)
        {
            try { Directory.Delete(item.Path, recursive: true); removed++; progress?.Report("Removed " + item.Path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { progress?.Report("Could not remove " + item.Path + ": " + e.Message); }
        }
        return removed;
    }

    /// <summary>
    /// Removes the previous copies of <paramref name="outputs"/> that are past the age limit, for a caller that holds no
    /// output lock (the desktop app when it opens). An output a job is using is skipped. Returns the folders removed.
    /// </summary>
    public static IReadOnlyList<string> ExpirePrevious(IEnumerable<string> outputs, int days, IProgress<string>? progress = null, DateTime? now = null)
    {
        var removed = new List<string>();
        foreach (string output in outputs.Select(HostPath.Canonical).Distinct())
        {
            // Nothing to do is the usual case, and then no lock file is written beside the output.
            if (OutputRetention.Expired(output, days, now).Count == 0) continue;
            try { using var guard = OutputLock.Acquire(output); removed.AddRange(OutputRetention.Expire(output, days, progress, now)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* in use: its job applies the limit */ }
        }
        return removed;
    }

    internal static long Size(string dir, CancellationToken ct)
    {
        long total = 0;
        foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        { ct.ThrowIfCancellationRequested(); if (file.LinkTarget is null) total += file.Length; }
        return total;
    }
}
