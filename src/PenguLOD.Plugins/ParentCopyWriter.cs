using System.Text;
using PenguLOD.Core.Pipeline;
using PenguLOD.Core.World;

namespace PenguLOD.Plugins;

/// <summary>
/// Writes <c>SKSE\Plugins\PenguLOD\PenguLOD.copies</c>, the Parent &gt; Child copies the PenguLOD SKSE plugin draws while the player is
/// in a walled city's child worldspace.
/// <code>
/// char[4] "PLCP", u32 version (2; v1 lacked alternate texture triples)
/// u32 stringCount, then per string: u16 byteLength + UTF-8 bytes   (plugin names, model paths, shape names, texture paths)
/// u32 entryCount, then per entry:
///   u32 worldPlugin (string index), u32 worldLocalId   (the child worldspace),
///   u32 model (string index, relative to Data\meshes), f32 pos[3], f32 rot[3] (radians), f32 scale,
///   u32 flags (1 = the child worldspace has a cell at these coordinates, 2 = the base record swaps textures),
///   u16 altCount (v2), then altCount * (u32 shapeName, u32 diffuse, u32 normal) where 0xFFFFFFFF = unchanged
/// </code>
/// </summary>
public static class ParentCopyWriter
{
    public const string RelativePath = "SKSE\\Plugins\\PenguLOD\\PenguLOD.copies";
    public const uint Version = 2;
    public const uint NoneIndex = 0xFFFF_FFFFu;

    public static int Write(string outputFolder, IReadOnlyList<ParentCopy> copies)
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

        static string? CleanTex(string? t)
        {
            if (string.IsNullOrWhiteSpace(t)) return null;
            var rel = GamePath.Normalize(t);
            return rel.StartsWith("textures\\", StringComparison.Ordinal) ? rel : "textures\\" + rel;
        }

        var rows = new List<(uint WorldPlugin, uint Model, ParentCopy C, List<(uint Shape, uint Diffuse, uint Normal)> Alts)>();
        foreach (var c in copies.OrderBy(c => c.ChildWorldspace, StringComparer.Ordinal).ThenBy(c => c.CellX).ThenBy(c => c.CellY)
                     .ThenBy(c => c.SourcePlugin, StringComparer.Ordinal).ThenBy(c => c.SourceLocalId))
        {
            var rel = GamePath.Normalize(c.Model);
            if (rel.StartsWith("meshes\\", StringComparison.Ordinal)) rel = rel["meshes\\".Length..];
            uint wp = Str(c.WorldPlugin);
            uint mp = Str(rel);
            var alts = new List<(uint Shape, uint Diffuse, uint Normal)>();
            if (c.AlternateTextures is { Count: > 0 } srcAlts)
            {
                foreach (var a in srcAlts)
                {
                    if (string.IsNullOrWhiteSpace(a.ShapeName)) continue;
                    var diff = CleanTex(a.Diffuse);
                    var norm = CleanTex(a.Normal);
                    if (diff is null && norm is null) continue;
                    alts.Add((Str(a.ShapeName), diff is not null ? Str(diff) : NoneIndex, norm is not null ? Str(norm) : NoneIndex));
                    if (alts.Count == ushort.MaxValue) break;
                }
            }
            rows.Add((wp, mp, c, alts));
        }

        var path = Path.Combine(outputFolder, RelativePath.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var fs = File.Create(path + ".tmp"))
        using (var w = new BinaryWriter(fs))
        {
            w.Write("PLCP"u8);
            w.Write(Version);
            w.Write((uint)strings.Count);
            foreach (var s in strings)
            {
                var b = Encoding.UTF8.GetBytes(s);
                w.Write((ushort)b.Length);
                w.Write(b);
            }
            w.Write((uint)rows.Count);
            foreach (var (worldPlugin, model, c, alts) in rows)
            {
                w.Write(worldPlugin); w.Write(c.WorldLocalId); w.Write(model);
                w.Write(c.Position.X); w.Write(c.Position.Y); w.Write(c.Position.Z);
                w.Write(c.RotationRadians.X); w.Write(c.RotationRadians.Y); w.Write(c.RotationRadians.Z);
                w.Write(c.Scale);
                w.Write(Flags(c));
                w.Write((ushort)alts.Count);
                foreach (var (shape, diff, norm) in alts)
                {
                    w.Write(shape); w.Write(diff); w.Write(norm);
                }
            }
        }
        File.Move(path + ".tmp", path, overwrite: true);
        return rows.Count;
    }

    public static uint Flags(ParentCopy c) => (c.ChildCellExists ? 1u : 0u) | ((c.HasAlternateTextures || c.AlternateTextures is { Count: > 0 }) ? 2u : 0u);

    /// <summary>Removes the file when the option is off, so an old one never lingers in the output.</summary>
    public static void Remove(string outputFolder)
    {
        var path = Path.Combine(outputFolder, RelativePath.Replace('\\', Path.DirectorySeparatorChar));
        if (File.Exists(path)) File.Delete(path);
    }
}
