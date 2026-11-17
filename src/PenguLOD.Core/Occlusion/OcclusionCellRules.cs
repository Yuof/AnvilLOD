namespace PenguLOD.Core.Occlusion;

/// <summary>
/// Which cells of a worldspace get occlusion data, as DynDOLOD's <c>OcclusionMode</c> names them: "All", or any of
/// "+TVDT" / "-TVDT" (only cells that already have occlusion data / only cells without), "-Flat" (not cells whose landscape
/// is perfectly flat) and "+Border" (only cells linked to a region with the Border Region flag, the playable area).
/// </summary>
public sealed record OcclusionCellMode(bool? Tvdt, bool SkipFlat, bool BorderOnly)
{
    public static OcclusionCellMode Parse(string text)
    {
        var parts = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new ArgumentException("The occlusion mode is empty.");
        if (parts.Length == 1 && parts[0].Equals("All", StringComparison.OrdinalIgnoreCase)) return new(null, false, false);
        bool? tvdt = null; bool flat = false, border = false;
        foreach (string part in parts)
            switch (part.ToLowerInvariant())
            {
                case "+tvdt" when tvdt is null: tvdt = true; break;
                case "-tvdt" when tvdt is null: tvdt = false; break;
                case "-flat" when !flat: flat = true; break;
                case "+border" when !border: border = true; break;
                default: throw new ArgumentException($"Unknown occlusion mode '{text}' (use All, or +TVDT or -TVDT, -Flat and +Border).");
            }
        return new(tvdt, flat, border);
    }

    public bool Includes(bool hasData, bool flat, bool inBorder) =>
        (Tvdt is null || Tvdt == hasData) && !(SkipFlat && flat) && (!BorderOnly || inBorder);

    public override string ToString()
    {
        var parts = new List<string>();
        if (Tvdt is { } t) parts.Add(t ? "+TVDT" : "-TVDT");
        if (SkipFlat) parts.Add("-Flat");
        if (BorderOnly) parts.Add("+Border");
        return parts.Count == 0 ? "All" : string.Join(' ', parts);
    }
}

/// <summary>
/// The occlusion mode of each worldspace, and the worldspaces switched on or off by name, as <c>DynDOLOD_SSE.ini</c> holds them
/// (<c>OcclusionMode</c>, <c>OcclusionMode_&lt;worldspace&gt;</c>, <c>Occlusion_&lt;worldspace&gt;</c>).
/// </summary>
public sealed class OcclusionWorldRules
{
    /// <summary>The option value that stands for DynDOLOD's own settings: its file when one is found, else what it ships with.</summary>
    public const string DynDolod = "DynDOLOD";

    public static readonly IReadOnlyList<string> Choices =
    [
        DynDolod, "All", "+Border", "-Flat", "-Flat +Border", "+TVDT", "+TVDT +Border", "+TVDT -Flat", "+TVDT -Flat +Border",
        "-TVDT", "-TVDT +Border", "-TVDT -Flat", "-TVDT -Flat +Border",
    ];

    // DynDOLOD_SSE.ini of DynDOLOD 3 Alpha-219.
    private static readonly string[] ShippedLines =
    [
        "OcclusionMode=-TVDT -Flat +Border", "Occlusion_ArnimaDagonRealm=0", "OcclusionMode_Tamriel=-Flat +Border",
        "OcclusionMode_Sovngarde=-TVDT -Flat", "OcclusionMode_DLC01SoulCairn=-TVDT -Flat",
    ];

    private readonly Dictionary<string, OcclusionCellMode> _modes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _enabled = new(StringComparer.OrdinalIgnoreCase);

    public OcclusionCellMode Default { get; private set; } = OcclusionCellMode.Parse("-TVDT -Flat +Border");

    /// <summary>What DynDOLOD ships with.</summary>
    public static OcclusionWorldRules Shipped => Read(ShippedLines);

    /// <summary>One mode for every worldspace.</summary>
    public static OcclusionWorldRules Uniform(string mode) => new() { Default = OcclusionCellMode.Parse(mode) };

    /// <summary>The rules for an option value: a mode for every worldspace, or DynDOLOD's own from its settings file (null or missing: as shipped).</summary>
    public static OcclusionWorldRules For(string choice, string? dynDolodIni = null) =>
        !choice.Equals(DynDolod, StringComparison.OrdinalIgnoreCase) ? Uniform(choice)
        : dynDolodIni is not null && File.Exists(dynDolodIni) ? Read(File.ReadLines(dynDolodIni))
        : Shipped;

    /// <summary>Reads the occlusion lines of a DynDOLOD settings file. A line that can't be read is skipped.</summary>
    public static OcclusionWorldRules Read(IEnumerable<string> lines)
    {
        var rules = new OcclusionWorldRules();
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0 || line[0] is ';' or '/' or '[') continue;
            string key = line[..eq].Trim(), value = line[(eq + 1)..].Trim();
            try
            {
                if (key.Equals("OcclusionMode", StringComparison.OrdinalIgnoreCase)) rules.Default = OcclusionCellMode.Parse(value);
                else if (key.StartsWith("OcclusionMode_", StringComparison.OrdinalIgnoreCase)) rules._modes[key["OcclusionMode_".Length..]] = OcclusionCellMode.Parse(value);
                else if (key.StartsWith("Occlusion_", StringComparison.OrdinalIgnoreCase) && value is "0" or "1") rules._enabled[key["Occlusion_".Length..]] = value == "1";
            }
            catch (ArgumentException) { }
        }
        return rules;
    }

    public OcclusionCellMode ModeFor(string worldspace) => _modes.GetValueOrDefault(worldspace, Default);

    /// <summary>True when the worldspace has a mode of its own.</summary>
    public bool Named(string worldspace) => _modes.ContainsKey(worldspace);

    /// <summary>True or false when the worldspace is switched on or off by name; null when the usual rules decide.</summary>
    public bool? Enabled(string worldspace) => _enabled.TryGetValue(worldspace, out bool on) ? on : null;

    public string Fingerprint => Default + "|" + string.Join(';', _modes.OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase).Select(m => m.Key + "=" + m.Value))
        + "|" + string.Join(';', _enabled.OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase).Select(m => m.Key + "=" + (m.Value ? 1 : 0)));
}
