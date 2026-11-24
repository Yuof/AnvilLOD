using System.Text;
using PenguLOD.Core.Lod;
using PenguLOD.Core.Pipeline;
using PenguLOD.Core.World;

namespace PenguLOD.Plugins;

/// <summary>
/// Writes <c>SKSE\Plugins\PenguLOD\PenguLOD.dyn</c>, the data file the PenguLOD SKSE plugin reads at startup.
/// <code>
/// char[4] "PLDY", u32 version (3; v1 had no grid objects, v2 had a single mesh index per entry)
/// u32 stringCount, then per string: u16 byteLength + UTF-8 bytes   (plugin names and mesh paths)
/// u32 entryCount, then per entry (68 bytes in v3; 60 bytes in v1-v2):
///   u32 refPlugin (string index), u32 refLocalId,
///   u32 worldspacePlugin (string index), u32 worldspaceLocalId,
///   u32 mesh (LOD4 / primary string index, relative to Data\meshes),
///   u32 mesh8 (v3: LOD8 string index, 0xFFFFFFFF = same / absent),
///   u32 mesh16 (v3: LOD16 string index, 0xFFFFFFFF = same / absent),
///   f32 pos[3], f32 rot[3] (radians), f32 scale,
///   u32 parentPlugin (string index, 0xFFFFFFFF = none), u32 parentLocalId,
///   u32 flags
/// </code>
/// </summary>
public static class DynamicLodWriter
{
    public const string RelativePath = "SKSE\\Plugins\\PenguLOD\\PenguLOD.dyn";
    public const uint Version = 3;
    public const uint NoneIndex = 0xFFFF_FFFFu;

    /// <param name="meshMap">Optional replacements for mesh paths (relative to Data\meshes, case-insensitive), e.g. water stand-ins.</param>
    public static int Write(string outputFolder, IReadOnlyList<DynamicLodReference> refs, IReadOnlyDictionary<string, string>? meshMap = null)
    {
        var strings = new List<string>();
        var index = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        uint Str(string s)
        {
            if (!index.TryGetValue(s, out var i))
            {
                i = (uint)strings.Count;
                strings.Add(s);
                index[s] = i;
            }
            return i;
        }

        string? CleanMesh(string? m)
        {
            if (string.IsNullOrEmpty(m) || m.StartsWith('~')) return null;
            m = ShipLodModels.StockMesh(m);
            var rel = GamePath.Normalize(m);
            if (rel.StartsWith("meshes\\", StringComparison.Ordinal)) rel = rel["meshes\\".Length..];
            if (meshMap is not null && meshMap.TryGetValue(rel, out var replacement)) rel = replacement;
            return rel;
        }

        var entries = new List<(uint P, uint Id, uint WP, uint WId, uint Mesh, uint Mesh8, uint Mesh16, LodReference R, DynamicLodReference D)>();
        foreach (var d in refs.OrderBy(d => d.Ref.FormKey, StringComparer.Ordinal))
        {
            var r = d.Ref;
            var m4 = CleanMesh(r.Meshes.Lod4);
            var m8 = CleanMesh(r.Meshes.Lod8);
            var m16 = CleanMesh(r.Meshes.Lod16);
            var m32 = CleanMesh(r.Meshes.Lod32);
            var primary = m4 ?? m8 ?? m16 ?? m32;
            if (string.IsNullOrEmpty(primary)) continue;

            uint pIdx = Str(d.RefPlugin);
            uint wpIdx = Str(d.WorldspacePlugin);
            uint meshIdx = Str(primary);
            uint mesh8Idx = !d.IsGridObject && !d.Glow && m8 is not null && !string.Equals(m8, primary, StringComparison.OrdinalIgnoreCase)
                ? Str(m8) : NoneIndex;
            string effective8 = mesh8Idx != NoneIndex ? m8! : primary;
            uint mesh16Idx = !d.IsGridObject && !d.Glow && m16 is not null && !string.Equals(m16, effective8, StringComparison.OrdinalIgnoreCase)
                ? Str(m16) : NoneIndex;
            if (d.ParentPlugin is { } parentPlugin) Str(parentPlugin);
            entries.Add((pIdx, d.RefLocalId, wpIdx, d.WorldspaceLocalId, meshIdx, mesh8Idx, mesh16Idx, r, d));
        }

        uint Str2(string s) => index[s];

        var path = Path.Combine(outputFolder, RelativePath.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var fs = File.Create(path + ".tmp"))
        using (var w = new BinaryWriter(fs))
        {
            w.Write("PLDY"u8);
            w.Write(Version);
            w.Write((uint)strings.Count);
            foreach (var s in strings)
            {
                var b = Encoding.UTF8.GetBytes(s);
                w.Write((ushort)b.Length);
                w.Write(b);
            }
            w.Write((uint)entries.Count);
            foreach (var e in entries)
            {
                w.Write(e.P); w.Write(e.Id); w.Write(e.WP); w.Write(e.WId);
                w.Write(e.Mesh); w.Write(e.Mesh8); w.Write(e.Mesh16);
                w.Write(e.R.Position.X); w.Write(e.R.Position.Y); w.Write(e.R.Position.Z);
                w.Write(e.R.RotationRadians.X); w.Write(e.R.RotationRadians.Y); w.Write(e.R.RotationRadians.Z);
                w.Write(e.R.Scale);
                w.Write(e.D.ParentPlugin is { } pp ? Str2(pp) : NoneIndex);
                w.Write(e.D.ParentLocalId);
                w.Write(Flags(e.D));
            }
        }
        File.Move(path + ".tmp", path, overwrite: true);

        var ini = Path.Combine(outputFolder, "SKSE", "Plugins", "PenguLOD.ini");
        if (!File.Exists(ini))
            File.WriteAllText(ini, DefaultIni);
        return entries.Count;
    }

    public static uint Flags(DynamicLodReference d) =>
        (d.ParentOpposite ? 1u : 0u) | (d.InitiallyDisabled ? 2u : 0u)
        | (d.Grid != DynamicGrid.None ? 4u : 0u) | (d.Grid == DynamicGrid.Near ? 8u : 0u) | (d.Grid == DynamicGrid.NeverFade ? 16u : 0u)
        | (d.Glow ? 32u : 0u) | (d.StandIn ? 64u : 0u);

    public const string DefaultIni = """
        ; PenguLOD SKSE plugin settings
        [Dynamic]
        ; Draw LOD for references that quests enable/disable (written by PenguLOD into PenguLOD\PenguLOD.dyn)
        bEnabled=1
        ; How far from the player dynamic LOD is drawn, in game units (4096 = one cell)
        fMaxDistance=120000
        ; How often enable states are checked, in milliseconds
        iUpdateIntervalMs=500
        ; Safety cap on how many dynamic LOD objects are drawn at once
        iMaxShown=8000
        ; Most objects loaded per update; the rest follow on the next updates, so entering a worldspace does not hitch
        iMaxShowPerUpdate=200
        ; Window glow of farmhouses, inns and forts beyond the loaded cells, lit at night (needs "Window glow" ticked when generating)
        bGlowWindows=1
        fGlowIntensity=1.0
        ; Water planes, waterfalls, fires, windmills and other DynDOLOD "grid" objects, drawn beyond the loaded cells
        bGridObjects=1
        ; How far "Near LOD" grid objects (water planes, creeks, fires) and "Far LOD" ones (waterfalls, windmills, ships) are drawn
        fNearGridDistance=40000
        fFarGridDistance=120000
        ; The real fires, candles and streams of PenguLOD Effects.esp are only switched on this close to the player (hundreds across the map crashed the renderer)
        fEffectsDistance=20000
        ; EXPERIMENTAL: keep them animated (waterfall flow, windmill blades). Can crash; off by default
        bAnimateExperimental=0
        iAnimationFps=30
        ; Log every object shown/hidden to Documents\My Games\Skyrim Special Edition\SKSE\PenguLOD.log
        bVerboseLog=0

        [LOD]
        ; LOD, fade and grass distances, applied in game like DynDOLOD DLL NG does. Values from DynDOLOD's MCM files
        ; (MCM\Config\DynDOLOD\settings.ini, MCM\Settings\DynDOLOD.ini) are used first; anything set here wins.
        ; Saving this file while the game runs applies the change within a couple of seconds.
        bApplyLodDistances=1
        ; If DynDOLOD.dll is still installed it manages these; set to 1 to let PenguLOD take over anyway
        bOverrideDynDOLOD=0
        ;fBlockLevel0Distance=60000
        ;fBlockLevel1Distance=90000
        ;fBlockMaximumDistance=250000
        ;fSplitDistanceMult=1.0
        ;fTreeLoadDistance=75000
        ;fSkyCellRefFadeDistance=150000
        ;fLODFadeOutMultObjects=15
        ;fLODFadeOutMultItems=10
        ;fLODFadeOutMultActors=15
        ;fLODFadeOutMultSkyCell=1
        ;fGrassStartFadeDistance=7000
        ;fGrassMaxStartFadeDistance=7000
        ;fGrassMinStartFadeDistance=0
        ;fGrassFadeRange=10000

        """;
}
