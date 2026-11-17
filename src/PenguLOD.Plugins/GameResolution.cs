using System.Globalization;
using Mutagen.Bethesda;
using PenguLOD.Core.World;
using PenguLOD.Core.Paths;
using PenguLOD.Plugins.Mo2;

namespace PenguLOD.Plugins;

/// <summary>
/// The resolution the game runs at, read from its settings files the way TexGen reads it to size LOD textures:
/// SSE Display Tweaks' <c>Resolution</c> when one is set, else <c>[Display] iSize W</c> and <c>iSize H</c> of SkyrimCustom.ini
/// or SkyrimPrefs.ini. Nothing is loaded; only settings files are opened.
/// </summary>
public static class GameResolution
{
    /// <param name="Source">The file the resolution was read from.</param>
    public sealed record Found(int Width, int Height, string Source);

    public static Found? Detect(GameContextOptions options)
    {
        string? P(string? path) => path is null ? null : HostPath.Resolve(path, winePrefix: options.WinePrefix);
        bool vr = options.Release == GameRelease.SkyrimVR;
        var settingsFolders = new List<string>();
        var modFolders = new List<string>();    // highest priority first
        try
        {
            if (options.Mode == GameSourceMode.Mo2Instance && options.Mo2InstanceFolder is { } instanceFolder)
            {
                var profile = Mo2Instance.Open(P(instanceFolder)!, options.Mo2Locations).OpenProfile(options.Mo2Profile);
                if (profile.UsesLocalInis) settingsFolders.Add(profile.Folder);
                modFolders.AddRange(profile.EnabledModFolders().Reverse());
                modFolders.Add(Path.Combine(profile.Instance.GamePath, "Data"));
            }
            else if (options.DataFolder is { } data)
            {
                string folder = P(data)!;
                if (GamePath.ResolveExisting(folder, "Data") is { } inner && Directory.Exists(inner)) folder = inner;
                modFolders.Add(folder);
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException) { }
        if (options.GameIniPath is { } ini && Path.GetDirectoryName(P(ini)) is { } beside) settingsFolders.Add(beside);
        if (options.PluginsTxt is { } plugins && Path.GetDirectoryName(Path.GetFullPath(P(plugins)!)) is { } pluginFolder) settingsFolders.Add(pluginFolder);
        settingsFolders.AddRange(HostPath.GameDocuments(options.WinePrefix, vr));

        foreach (string mod in modFolders)
            foreach (string name in new[] { "SSEDisplayTweaks_Custom.ini", "SSEDisplayTweaks.ini" })
                if (GamePath.ResolveExisting(mod, @"SKSE\Plugins\" + name) is { } file && File.Exists(file) && DisplayTweaks(file) is { } tweaked) return tweaked;
        foreach (string name in new[] { "SkyrimCustom.ini", "SkyrimPrefs.ini" })
            foreach (string folder in settingsFolders)
                if (Directory.Exists(folder) && GamePath.ResolveExisting(folder, name) is { } file && File.Exists(file) && Display(file) is { } found) return found;
        return null;
    }

    private static Found? Display(string file)
    {
        if (!Mo2Instance.ReadIni(file).TryGetValue("Display", out var display)) return null;
        return display.TryGetValue("iSize W", out var w) && display.TryGetValue("iSize H", out var h) ? Valid(w, h, file) : null;
    }

    // "Resolution=1920x1080" in [Render]; a commented line ("#Resolution=") leaves the game's own setting in charge.
    private static Found? DisplayTweaks(string file)
    {
        if (!Mo2Instance.ReadIni(file).TryGetValue("Render", out var render) || !render.TryGetValue("Resolution", out var value)) return null;
        var parts = value.Split(['x', 'X'], StringSplitOptions.TrimEntries);
        return parts.Length == 2 ? Valid(parts[0], parts[1], file) : null;
    }

    private static Found? Valid(string width, string height, string file) =>
        int.TryParse(width, NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) && int.TryParse(height, NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)
        && w is >= 320 and <= 32768 && h is >= 200 and <= 32768 ? new Found(w, h, file) : null;
}
