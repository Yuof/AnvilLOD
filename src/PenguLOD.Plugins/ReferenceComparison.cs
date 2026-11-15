using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using PenguLOD.Core;
using PenguLOD.Core.Pipeline;
using PenguLOD.Meshes.Nif;
using PenguLOD.Textures.Dds;

namespace PenguLOD.Plugins;

/// <summary>Blocks of one worldspace and level that both sides have: totals over them, and shapes and textures per block.</summary>
public sealed record BlockParity(string World, int Level, string Kind, int Common, int OnlyOutput, int OnlyReference,
    long OutputBytes, long ReferenceBytes, long OutputTriangles, long ReferenceTriangles,
    double OutputShapesPerBlock, double ReferenceShapesPerBlock, double OutputTexturesPerBlock, double ReferenceTexturesPerBlock);

/// <summary>What the output's common blocks are made of, by layer.</summary>
public sealed record LayerParity(string World, int Level, string Kind, string Layer, long Triangles, long Shapes);

/// <summary>How often a combination of shape name and shader flags occurs on one side.</summary>
public sealed record ShaderParity(string Side, string Layer, string ShapeName, string Flags1, string Flags2, long Shapes);

/// <summary>
/// Textures both sides have under the same path. The pixel ratio is output / reference; brightness and contrast are ratios of the
/// mean and the spread of luminance over opaque pixels, measured on a sample of colour textures reduced to the same size.
/// </summary>
public sealed record TextureParity(string Group, int Common, int OnlyOutput, int OnlyReference, double? MedianPixelRatio,
    int Sampled, double? MedianBrightnessRatio, double? MedianContrastRatio);

/// <summary>Record counts by type of one plugin.</summary>
public sealed record PluginRecords(string Side, string File, long Bytes, IReadOnlyDictionary<string, int> Records);

/// <summary>
/// Measures a PenguLOD output against output of DynDOLOD, TexGen or xLODGen for the same game, from the files alone: blocks under
/// the same name, textures under the same path, the plugins of each side, and names in the output that differ only in case. It needs
/// no load order, so the two sides may come from different days; differences in what is installed show up as differences here.
/// </summary>
public sealed class ReferenceComparison
{
    public int Schema { get; set; } = 1;
    public string Version { get; set; } = ProductInfo.Version;
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public List<string> Outputs { get; set; } = [];
    public List<string> References { get; set; } = [];
    public List<BlockParity> Blocks { get; set; } = [];
    public List<LayerParity> Layers { get; set; } = [];
    public List<ShaderParity> Shaders { get; set; } = [];
    public List<TextureParity> Textures { get; set; } = [];
    public List<PluginRecords> Plugins { get; set; } = [];
    /// <summary>Worldspaces with blocks on one side only.</summary>
    public List<string> WorldsOnlyInOutput { get; set; } = [];
    public List<string> WorldsOnlyInReference { get; set; } = [];
    /// <summary>Names in an output folder that differ only in case, one group per line.</summary>
    public List<string> CaseConflicts { get; set; } = [];
    public List<string> Warnings { get; set; } = [];

    private sealed record Block(string World, int Level, string Kind);
    private sealed record Measured(long Bytes, IReadOnlyList<ShapeCost> Shapes);

    public static ReferenceComparison Compare(IReadOnlyList<string> outputs, IReadOnlyList<string> references,
        IReadOnlyCollection<string>? worlds = null, int textureSamples = 60, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (outputs.Count == 0 || references.Count == 0) throw new ArgumentException("Give at least one output folder and one reference folder.");
        foreach (string folder in outputs.Concat(references))
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Folder not found: " + folder);
        if (textureSamples is < 0 or > 10000) throw new ArgumentOutOfRangeException(nameof(textureSamples));
        var result = new ReferenceComparison { Outputs = [.. outputs], References = [.. references] };
        var only = worlds is { Count: > 0 } ? new HashSet<string>(worlds, StringComparer.OrdinalIgnoreCase) : null;

        var ours = Index(outputs, ct); var theirs = Index(references, ct);
        CompareBlocks(result, ours, theirs, only, progress, ct);
        CompareTextures(result, ours, theirs, textureSamples, progress, ct);
        foreach (var (side, folders) in new[] { ("output", outputs), ("reference", references) })
            foreach (string folder in folders)
                foreach (string file in Directory.EnumerateFiles(folder).Where(IsPlugin).Order(StringComparer.OrdinalIgnoreCase))
                {
                    ct.ThrowIfCancellationRequested();
                    try { result.Plugins.Add(new(side, Path.GetFileName(file), new FileInfo(file).Length, CountRecords(file))); }
                    catch (Exception e) when (e is IOException or InvalidDataException)
                    { result.Warnings.Add(Path.GetFileName(file) + ": " + e.Message); }
                }
        foreach (string folder in outputs)
            foreach (var group in OutputCase.Conflicts(folder))
                result.CaseConflicts.Add(string.Join(", ", group));
        return result;
    }

    /// <summary>Files of several folders under one case-insensitive relative path ('/'-separated, lower case); the first folder wins.</summary>
    private static Dictionary<string, string> Index(IReadOnlyList<string> folders, CancellationToken ct)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string folder in folders)
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                string extension = Path.GetExtension(file).ToLowerInvariant();
                if (extension is not (".bto" or ".btr" or ".dds")) continue;
                index.TryAdd(Path.GetRelativePath(folder, file).Replace('\\', '/').ToLowerInvariant(), file);
            }
        return index;
    }

    private static bool IsPlugin(string file) => Path.GetExtension(file).ToLowerInvariant() is ".esp" or ".esm" or ".esl";

    private static Block? ParseBlock(string relative)
    {
        string extension = Path.GetExtension(relative);
        if (extension is not (".bto" or ".btr")) return null;
        string[] parts = Path.GetFileNameWithoutExtension(relative).Split('.');
        return parts.Length >= 4 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int level)
            ? new(parts[0], level, extension == ".btr" ? "terrain" : "objects") : null;
    }

    private static void CompareBlocks(ReferenceComparison result, Dictionary<string, string> ours, Dictionary<string, string> theirs,
        HashSet<string>? only, IProgress<string>? progress, CancellationToken ct)
    {
        // Blocks are matched by lower-case path; a worldspace is shown as the output spells it, or the reference when only it has it.
        var spelling = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in ours.Values.Concat(theirs.Values))
            if (ParseBlock(Path.GetFileName(file).ToLowerInvariant()) is not null) spelling.TryAdd(Path.GetFileName(file).Split('.')[0], Path.GetFileName(file).Split('.')[0]);
        Dictionary<string, Block> Blocks(Dictionary<string, string> index) => index.Keys
            .Select(k => (Key: k, Block: ParseBlock(k))).Where(x => x.Block is not null && (only is null || only.Contains(x.Block.World)))
            .ToDictionary(x => x.Key, x => x.Block! with { World = spelling[x.Block!.World] }, StringComparer.Ordinal);
        var mine = Blocks(ours); var reference = Blocks(theirs);
        var myWorlds = mine.Values.Select(b => b.World).ToHashSet(StringComparer.Ordinal);
        var referenceWorlds = reference.Values.Select(b => b.World).ToHashSet(StringComparer.Ordinal);
        result.WorldsOnlyInOutput = [.. myWorlds.Except(referenceWorlds).Order(StringComparer.Ordinal)];
        result.WorldsOnlyInReference = [.. referenceWorlds.Except(myWorlds).Order(StringComparer.Ordinal)];

        var common = mine.Keys.Where(reference.ContainsKey).Order(StringComparer.Ordinal).ToArray();
        var measured = new ConcurrentDictionary<string, (Measured? Output, Measured? Reference)>(StringComparer.Ordinal);
        var warnings = new ConcurrentBag<string>();
        int done = 0;
        Parallel.ForEach(common, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8), CancellationToken = ct }, key =>
        {
            Measured? Measure(string file, string kind)
            {
                try { return new(new FileInfo(file).Length, NifCostReader.ReadShapes(File.ReadAllBytes(file), kind)); }
                catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or ArgumentException)
                { warnings.Add(key + ": " + e.Message); return null; }
            }
            measured[key] = (Measure(ours[key], mine[key].Kind), Measure(theirs[key], mine[key].Kind));
            int n = Interlocked.Increment(ref done);
            if (n % 200 == 0) progress?.Report($"Comparing blocks: {n:N0} of {common.Length:N0}...");
        });
        result.Warnings.AddRange(warnings.Order(StringComparer.Ordinal));

        foreach (var group in mine.Values.Concat(reference.Values).Distinct().Where(b => myWorlds.Contains(b.World) && referenceWorlds.Contains(b.World))
                     .OrderBy(b => b.World, StringComparer.Ordinal).ThenBy(b => b.Kind, StringComparer.Ordinal).ThenBy(b => b.Level))
        {
            var pairs = common.Where(k => mine[k] == group).Select(k => measured[k]).Where(p => p.Output is not null && p.Reference is not null).ToArray();
            int onlyOutput = mine.Count(kv => kv.Value == group && !reference.ContainsKey(kv.Key));
            int onlyReference = reference.Count(kv => kv.Value == group && !mine.ContainsKey(kv.Key));
            static double PerBlock(IEnumerable<Measured> blocks, Func<Measured, int> count) => blocks.Select(count).DefaultIfEmpty(0).Average();
            static int Textures(Measured m) => m.Shapes.Select(s => s.Texture).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).Count();
            result.Blocks.Add(new(group.World, group.Level, group.Kind, pairs.Length, onlyOutput, onlyReference,
                pairs.Sum(p => p.Output!.Bytes), pairs.Sum(p => p.Reference!.Bytes),
                pairs.Sum(p => p.Output!.Shapes.Sum(s => (long)s.Triangles)), pairs.Sum(p => p.Reference!.Shapes.Sum(s => (long)s.Triangles)),
                PerBlock(pairs.Select(p => p.Output!), m => m.Shapes.Count), PerBlock(pairs.Select(p => p.Reference!), m => m.Shapes.Count),
                PerBlock(pairs.Select(p => p.Output!), Textures), PerBlock(pairs.Select(p => p.Reference!), Textures)));
            foreach (var layer in pairs.SelectMany(p => p.Output!.Shapes).GroupBy(s => s.Layer).OrderBy(g => g.Key, StringComparer.Ordinal))
                result.Layers.Add(new(group.World, group.Level, group.Kind, layer.Key, layer.Sum(s => (long)s.Triangles), layer.LongCount()));
        }

        foreach (var (side, shapes) in new[]
                 {
                     ("output", measured.Values.Where(p => p.Output is not null && p.Reference is not null).SelectMany(p => p.Output!.Shapes)),
                     ("reference", measured.Values.Where(p => p.Output is not null && p.Reference is not null).SelectMany(p => p.Reference!.Shapes)),
                 })
            result.Shaders.AddRange(shapes.GroupBy(s => (s.Layer, s.Name, s.ShaderFlags1, s.ShaderFlags2))
                .Select(g => new ShaderParity(side, g.Key.Layer, g.Key.Name, g.Key.ShaderFlags1.ToString("X8"), g.Key.ShaderFlags2.ToString("X8"), g.LongCount()))
                .OrderByDescending(s => s.Shapes).ThenBy(s => s.Layer, StringComparer.Ordinal).ThenBy(s => s.ShapeName, StringComparer.Ordinal)
                .ThenBy(s => s.Flags1, StringComparer.Ordinal).ThenBy(s => s.Flags2, StringComparer.Ordinal).Take(20));
    }

    private static string TextureGroup(string relative) =>
        relative.StartsWith("textures/terrain/lodgen/", StringComparison.Ordinal) ? "billboards"
        : relative.StartsWith("textures/dyndolod/lod/trees/", StringComparison.Ordinal) ? "trunks"
        : relative.StartsWith("textures/terrain/", StringComparison.Ordinal) ? "terrain"
        : relative.StartsWith("textures/lod/", StringComparison.Ordinal) || relative.Contains("/lod/", StringComparison.Ordinal) ? "object LOD"
        : "other";

    private static void CompareTextures(ReferenceComparison result, Dictionary<string, string> ours, Dictionary<string, string> theirs,
        int samples, IProgress<string>? progress, CancellationToken ct)
    {
        static bool IsTexture(string k) => k.EndsWith(".dds", StringComparison.Ordinal);
        var groups = ours.Keys.Where(IsTexture).Concat(theirs.Keys.Where(IsTexture)).Distinct(StringComparer.Ordinal)
            .GroupBy(TextureGroup).OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            var common = group.Where(k => ours.ContainsKey(k) && theirs.ContainsKey(k)).Order(StringComparer.Ordinal).ToArray();
            int onlyOutput = group.Count(k => !theirs.ContainsKey(k)), onlyReference = group.Count(k => !ours.ContainsKey(k));
            var pixels = new List<double>();
            foreach (string key in common)
            {
                if (Size(ours[key]) is { } a && Size(theirs[key]) is { } b && b > 0) pixels.Add(a / (double)b);
            }
            // Colour textures only: a normal map has no brightness to compare.
            var colour = common.Where(k => !k.EndsWith("_n.dds", StringComparison.Ordinal)).ToArray();
            var picked = samples == 0 || colour.Length <= samples ? (samples == 0 ? [] : colour)
                : Enumerable.Range(0, samples).Select(i => colour[(int)((long)i * colour.Length / samples)]).ToArray();
            var brightness = new ConcurrentBag<double>(); var contrast = new ConcurrentBag<double>();
            if (picked.Length > 0) progress?.Report($"Comparing {picked.Length:N0} {group.Key} textures...");
            Parallel.ForEach(picked, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8), CancellationToken = ct }, key =>
            {
                if (Light(ours[key]) is not { } mine || Light(theirs[key]) is not { } reference || reference.Mean < 1) return;
                brightness.Add(mine.Mean / reference.Mean);
                if (reference.Spread >= 1) contrast.Add(mine.Spread / reference.Spread);
            });
            result.Textures.Add(new(group.Key, common.Length, onlyOutput, onlyReference, Median(pixels),
                brightness.Count, Median(brightness), Median(contrast)));
        }
    }

    private static long? Size(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            byte[] header = new byte[Math.Min(148, stream.Length)]; stream.ReadExactly(header);
            var info = DdsFile.ReadInfo(header);
            return (long)info.Width * info.Height;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or ArgumentException) { return null; }
    }

    /// <summary>
    /// Mean and standard deviation of luminance over the opaque part of a texture's largest mip, after reducing it to at most
    /// 64 texels a side, so two sizes of the same picture give comparable figures. Null when it cannot be decoded or is empty.
    /// </summary>
    public static (double Mean, double Spread)? Light(string file)
    {
        byte[] rgba; int width, height;
        try { (rgba, width, height, _) = DdsFile.DecodeTopMip(File.ReadAllBytes(file)); }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or ArgumentException or IndexOutOfRangeException) { return null; }
        return Light(rgba, width, height);
    }

    public static (double Mean, double Spread)? Light(byte[] rgba, int width, int height)
    {
        int stepX = Math.Max(1, (width + 63) / 64), stepY = Math.Max(1, (height + 63) / 64);
        var values = new List<double>();
        for (int y0 = 0; y0 < height; y0 += stepY)
            for (int x0 = 0; x0 < width; x0 += stepX)
            {
                double sum = 0; int opaque = 0, total = 0;
                for (int y = y0; y < Math.Min(height, y0 + stepY); y++)
                    for (int x = x0; x < Math.Min(width, x0 + stepX); x++)
                    {
                        int i = (y * width + x) * 4; total++;
                        if (rgba[i + 3] <= 127) continue;
                        opaque++; sum += 0.2126 * rgba[i] + 0.7152 * rgba[i + 1] + 0.0722 * rgba[i + 2];
                    }
                if (opaque * 2 >= total && opaque > 0) values.Add(sum / opaque);
            }
        if (values.Count < 4) return null;
        double mean = values.Average();
        return (mean, Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Count));
    }

    private static double? Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? null : sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    /// <summary>Counts the records of a plugin by type from the record and group headers alone.</summary>
    public static IReadOnlyDictionary<string, int> CountRecords(string file)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        using var stream = File.OpenRead(file);
        byte[] header = new byte[24];
        long position = 0, length = stream.Length;
        while (position < length)
        {
            if (position + 24 > length) throw new InvalidDataException("Truncated record header.");
            stream.Position = position; stream.ReadExactly(header);
            if (!header.AsSpan(0, 4).ToArray().All(c => c is >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'_'))
                throw new InvalidDataException("Not a plugin record at offset " + position + ".");
            string type = Encoding.ASCII.GetString(header, 0, 4);
            uint size = BitConverter.ToUInt32(header, 4);
            if (type == "GRUP")
            {
                if (size < 24) throw new InvalidDataException("Invalid group size.");
                position += 24;      // the group's records follow its header
            }
            else
            {
                counts[type] = counts.GetValueOrDefault(type) + 1;
                position += 24 + size;
            }
        }
        return counts;
    }

    public string Summary()
    {
        var text = new StringBuilder();
        static string Ratio(double a, double b) => b > 0 ? (a / b).ToString("0.00", CultureInfo.InvariantCulture) + "x" : "-";
        static string Number(double? v) => v is { } d ? d.ToString("0.00", CultureInfo.InvariantCulture) : "-";
        text.AppendLine("Blocks both sides have (output / reference):");
        text.AppendLine($"  {"world",-22} {"kind",-8} {"LOD",3} {"blocks",7} {"triangles",27} {"ratio",7} {"shapes per block",18} {"textures per block",19} {"only out",9} {"only ref",9}");
        void Row(BlockParity b) =>
            text.AppendLine($"  {b.World,-22} {b.Kind,-8} {b.Level,3} {b.Common,7:N0} {b.OutputTriangles.ToString("N0") + " / " + b.ReferenceTriangles.ToString("N0"),27} {Ratio(b.OutputTriangles, b.ReferenceTriangles),7}"
                + $" {b.OutputShapesPerBlock.ToString("0.0", CultureInfo.InvariantCulture) + " / " + b.ReferenceShapesPerBlock.ToString("0.0", CultureInfo.InvariantCulture),18}"
                + $" {b.OutputTexturesPerBlock.ToString("0.0", CultureInfo.InvariantCulture) + " / " + b.ReferenceTexturesPerBlock.ToString("0.0", CultureInfo.InvariantCulture),19} {b.OnlyOutput,9:N0} {b.OnlyReference,9:N0}");
        // Rows without a block on both sides say nothing; they stay in the report file.
        var shown = Blocks.Where(b => b.Common > 0).ToList();
        var worldsShown = shown.Select(b => b.World).Distinct().ToList();
        if (worldsShown.Count > 1)
            foreach (var all in shown.GroupBy(b => (b.Kind, b.Level)).OrderBy(g => g.Key.Kind, StringComparer.Ordinal).ThenBy(g => g.Key.Level))
            {
                int blocks = all.Sum(b => b.Common);
                Row(new($"all {worldsShown.Count} worldspaces", all.Key.Level, all.Key.Kind, blocks, all.Sum(b => b.OnlyOutput), all.Sum(b => b.OnlyReference),
                    all.Sum(b => b.OutputBytes), all.Sum(b => b.ReferenceBytes), all.Sum(b => b.OutputTriangles), all.Sum(b => b.ReferenceTriangles),
                    all.Sum(b => b.OutputShapesPerBlock * b.Common) / blocks, all.Sum(b => b.ReferenceShapesPerBlock * b.Common) / blocks,
                    all.Sum(b => b.OutputTexturesPerBlock * b.Common) / blocks, all.Sum(b => b.ReferenceTexturesPerBlock * b.Common) / blocks));
            }
        // The largest worldspaces by what the reference has; the rest are in the report file.
        var largest = shown.GroupBy(b => b.World).OrderByDescending(g => g.Sum(b => b.ReferenceTriangles)).Take(4).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var b in shown.Where(b => largest.Contains(b.World))) Row(b);
        if (worldsShown.Count > largest.Count) text.AppendLine($"  ({worldsShown.Count - largest.Count} smaller worldspaces are in the report file)");
        if (shown.Count == 0) text.AppendLine("  none");
        if (Layers.Any(l => l.Kind == "objects"))
        {
            text.AppendLine("Output layers in those object blocks (share of triangles, shapes):");
            foreach (var level in Layers.Where(l => l.Kind == "objects" && largest.Contains(l.World)).GroupBy(l => (l.World, l.Level)))
            {
                long all = Math.Max(1, level.Sum(l => l.Triangles));
                text.AppendLine($"  {level.Key.World} LOD{level.Key.Level}: " + string.Join(", ", level.OrderByDescending(l => l.Triangles)
                    .Select(l => $"{l.Layer} {100.0 * l.Triangles / all:0}% ({l.Shapes:N0} shapes)")));
            }
        }
        if (Shaders.Count > 0)
        {
            text.AppendLine("Most used shape names and shader flags:");
            foreach (var s in Shaders.GroupBy(s => s.Side).SelectMany(g => g.Take(8)))
                text.AppendLine($"  {s.Side,-9} {s.Layer,-10} {s.ShapeName,-14} {s.Flags1}/{s.Flags2} {s.Shapes,9:N0} shapes");
        }
        text.AppendLine("Textures under the same path (output / reference):");
        foreach (var t in Textures)
            text.AppendLine($"  {t.Group,-11} {t.Common,6:N0} common, {t.OnlyOutput,6:N0} only output, {t.OnlyReference,6:N0} only reference; pixels {Number(t.MedianPixelRatio)}x"
                + (t.Sampled > 0 ? $", brightness {Number(t.MedianBrightnessRatio)}x, contrast {Number(t.MedianContrastRatio)}x ({t.Sampled} sampled)" : ""));
        text.AppendLine("Plugins:");
        foreach (var p in Plugins)
            text.AppendLine($"  {p.Side,-9} {p.File,-26} {p.Bytes / 1048576.0,8:N1} MiB  " + string.Join(", ", p.Records.OrderByDescending(r => r.Value).Take(8).Select(r => $"{r.Key} {r.Value:N0}")));
        if (WorldsOnlyInOutput.Count > 0) text.AppendLine($"Worldspaces only in the output: {WorldsOnlyInOutput.Count:N0}");
        if (WorldsOnlyInReference.Count > 0) text.AppendLine("Worldspaces only in the reference: " + string.Join(", ", WorldsOnlyInReference));
        text.AppendLine(CaseConflicts.Count == 0 ? "Output paths: no names that differ only in case."
            : $"Output paths: {CaseConflicts.Count:N0} names differ only in case (first: {CaseConflicts[0]}).");
        if (Warnings.Count > 0) text.AppendLine($"{Warnings.Count:N0} files could not be measured (first: {Warnings[0]}).");
        return text.ToString().TrimEnd();
    }
}
