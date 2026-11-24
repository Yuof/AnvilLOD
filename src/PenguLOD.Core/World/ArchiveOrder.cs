namespace PenguLOD.Core.World;

/// <summary>
/// Our own archive (.bsa) priority order for a plain Data folder, used when Mutagen's ordering throws (its archive-name
/// comparer is unimplemented for some names, which is how a 3,600-plugin Steam install failed on its first step).
/// The engine loads the base game's archives first, then every archive whose name starts with a plugin's name, in the
/// order of the load order; later archives win. Archives that belong to no plugin sort among the base game's, by name.
/// </summary>
public static class ArchiveOrder
{
    /// <summary>
    /// Intra-plugin BSA tie-breaker matching Skyrim SE: the exact <c>&lt;PluginBase&gt;.bsa</c> loads first (lowest priority),
    /// followed by suffixed archives such as <c>&lt;PluginBase&gt; - Textures.bsa</c> in case-insensitive alphabetical order.
    /// </summary>
    public static (int Suffixed, string FileName) IntraPluginKey(string archivePathOrName, string pluginBase)
    {
        var fileName = GamePath.GetFileName(archivePathOrName);
        var stem = GamePath.GetFileNameWithoutExtension(fileName);
        int suffixed = stem.Equals(pluginBase, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        return (suffixed, fileName);
    }

    /// <param name="archiveFiles">Full paths of the .bsa files in the Data folder.</param>
    /// <param name="pluginOrder">Plugin file names in load order (lowest priority first).</param>
    /// <returns>The archive paths, lowest priority first.</returns>
    public static List<string> LowToHigh(IEnumerable<string> archiveFiles, IReadOnlyList<string> pluginOrder)
    {
        // Longest plugin base name first, so "Skyrim - Textures" can't be taken by a plugin called "Skyrim".
        var bases = pluginOrder
            .Select((name, index) => (Base: GamePath.GetFileNameWithoutExtension(name), Index: index))
            .Where(p => p.Base.Length > 0)
            .OrderByDescending(p => p.Base.Length)
            .ToList();

        (int Rank, int Suffixed, string FileName) Key(string archivePath)
        {
            var fileName = GamePath.GetFileName(archivePath);
            var stem = GamePath.GetFileNameWithoutExtension(fileName);
            foreach (var (b, index) in bases)
                if (stem.StartsWith(b, StringComparison.OrdinalIgnoreCase)
                    && (stem.Length == b.Length || !char.IsLetterOrDigit(stem[b.Length])))
                    return (index + 1, stem.Length == b.Length ? 0 : 1, fileName);
            return (0, 0, fileName);   // no plugin: with the base game
        }

        return archiveFiles
            .Select(p => (Path: p, Sort: Key(p)))
            .OrderBy(t => t.Sort.Rank)
            .ThenBy(t => t.Sort.Suffixed)
            .ThenBy(t => t.Sort.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(t => t.Path)
            .ToList();
    }
}
