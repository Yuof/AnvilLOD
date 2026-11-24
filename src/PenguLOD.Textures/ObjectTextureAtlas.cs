using PenguLOD.Core.Pipeline;
using PenguLOD.Core.Textures;
using PenguLOD.Textures.Bc;
using PenguLOD.Textures.Dds;

namespace PenguLOD.Textures;

/// <summary>Deterministic paired diffuse/normal atlas pages with isolated rectangles and bounded mip chains.</summary>
public static class ObjectTextureAtlas
{
    public sealed record Input(string Key, TextureImage Diffuse, TextureImage? Normal, bool Alpha, uint ClampMode, int AlphaCutoff = 128,
        IReadOnlyDictionary<int, TextureImage>? Extra = null, IReadOnlySet<int>? SrgbSlots = null);
    public sealed record Placement(int Page, AtlasRect Rect);
    public sealed record BoxPlacement(int Page, int Ox, int Oy, int OuterW, int OuterH, int InnerW, int InnerH, int Gutter, int PageW, int PageH);
    /// <param name="Size">Width in pixels.</param>
    /// <param name="Height">Height in pixels: the width, or half of it when the rectangles fill no more than the upper half.</param>
    public sealed record Page(byte[] Diffuse, byte[] Normal, int Size, int Mips, IReadOnlyDictionary<int, byte[]>? Extra = null, int Height = 0);
    public sealed record Result(IReadOnlyList<Page> Pages, IReadOnlyDictionary<string, Placement> Placements,
        IReadOnlyDictionary<string, BoxPlacement>? Boxes = null);

    public static Result Build(IReadOnlyList<Input> inputs, int cellSize = 256, int pageSize = 2048, CancellationToken ct = default,
        bool variableAspect = false, IReadOnlyList<(int Width, int Height, IReadOnlyList<(string Key, int Ox, int Oy, int OuterW, int OuterH, int InnerW, int InnerH, int Gutter)> Pinned)>? pinnedPages = null)
    {
        if (cellSize is < 64 or > 512 || (cellSize & (cellSize - 1)) != 0 || pageSize < cellSize || pageSize > 8192 || (pageSize & (pageSize - 1)) != 0)
            throw new ArgumentException("Atlas dimensions must be powers of two; cells 64–512, pages up to 8192.");
        if (inputs.Select(i => i.Key).Distinct(StringComparer.Ordinal).Count() != inputs.Count)
            throw new ArgumentException("Duplicate atlas input keys.");
        string Channels(Input input) => string.Join(',', (input.Extra?.Keys ?? []).Order()) + ":" + string.Join(',', (input.SrgbSlots ?? new HashSet<int>()).Order());
        if (inputs.Select(Channels).Distinct().Skip(1).Any())
            throw new ArgumentException("Atlas inputs must have matching auxiliary slots and color spaces.");
        if (inputs.Count == 0)
            return new([], new Dictionary<string, Placement>(StringComparer.Ordinal), new Dictionary<string, BoxPlacement>(StringComparer.Ordinal));

        if (!variableAspect && pinnedPages is null)
            return BuildUniform(inputs, cellSize, pageSize, ct);
        return BuildVariable(inputs, cellSize, pageSize, ct, pinnedPages);
    }

    private static Result BuildUniform(IReadOnlyList<Input> inputs, int cellSize, int pageSize, CancellationToken ct)
    {
        var placements = new Dictionary<string, Placement>(StringComparer.Ordinal);
        var boxes = new Dictionary<string, BoxPlacement>(StringComparer.Ordinal);
        var pages = new List<Page>();
        int across = pageSize / cellSize, perPage = across * across, gutter = cellSize / 16, inner = cellSize - 2 * gutter;
        var sorted = inputs.OrderBy(i => i.Key, StringComparer.Ordinal).ToArray();
        for (int start = 0; start < sorted.Length; start += perPage)
        {
            ct.ThrowIfCancellationRequested();
            var subset = sorted.Skip(start).Take(perPage).ToArray();
            int grid = 1; while (grid * grid < subset.Length) grid *= 2;
            int rows = grid > 1 && subset.Length <= grid * grid / 2 ? grid / 2 : grid;
            int size = grid * cellSize, height = rows * cellSize;
            var packed = new List<(Input Source, int Ox, int Oy, int OuterW, int OuterH, int InnerW, int InnerH, int Gutter)>(subset.Length);
            for (int i = 0; i < subset.Length; i++)
                packed.Add((subset[i], (i % grid) * cellSize, (i / grid) * cellSize, cellSize, cellSize, inner, inner, gutter));
            EncodePage(packed, size, height, maxShifts: BitOperationsLog2(cellSize / 8), pages, placements, boxes, ct);
        }
        return new(pages, placements, boxes);
    }

    private static Result BuildVariable(IReadOnlyList<Input> inputs, int cellSize, int pageSize, CancellationToken ct,
        IReadOnlyList<(int Width, int Height, IReadOnlyList<(string Key, int Ox, int Oy, int OuterW, int OuterH, int InnerW, int InnerH, int Gutter)> Pinned)>? pinnedPages)
    {
        int gutter = Math.Clamp(cellSize / 16, 4, 16);
        int maxInner = cellSize - 2 * gutter;
        int minInner = gutter * 2;
        int AlignGutter(int v) => ((v + gutter - 1) / gutter) * gutter;

        var byKey = inputs.ToDictionary(i => i.Key, StringComparer.Ordinal);
        (int InnerW, int InnerH, int OuterW, int OuterH) Measure(Input src)
        {
            int sw = Math.Max(4, src.Diffuse.Width), sh = Math.Max(4, src.Diffuse.Height);
            float scale = Math.Min(1f, (float)maxInner / Math.Max(sw, sh));
            int iw = Math.Clamp(AlignGutter((int)MathF.Round(sw * scale)), minInner, maxInner);
            int ih = Math.Clamp(AlignGutter((int)MathF.Round(sh * scale)), minInner, maxInner);
            return (iw, ih, iw + 2 * gutter, ih + 2 * gutter);
        }

        var assignedKeys = new HashSet<string>(StringComparer.Ordinal);
        var pageSpecs = new List<(int Width, int Height, List<(Input Source, int Ox, int Oy, int OuterW, int OuterH, int InnerW, int InnerH, int Gutter)> Items, List<int[]> Skylines)>();

        if (pinnedPages is not null)
        {
            foreach (var p in pinnedPages)
            {
                if (p.Width > pageSize || p.Height > pageSize || p.Width < gutter || p.Height < gutter) continue;
                int cols = p.Width / gutter;
                var skyline = new int[cols];
                var items = new List<(Input Source, int Ox, int Oy, int OuterW, int OuterH, int InnerW, int InnerH, int Gutter)>();
                foreach (var box in p.Pinned)
                {
                    if (!byKey.TryGetValue(box.Key, out var src) || !assignedKeys.Add(box.Key)) continue;
                    if (box.Ox < 0 || box.Oy < 0 || box.Ox + box.OuterW > p.Width || box.Oy + box.OuterH > p.Height || box.Gutter != gutter)
                    {
                        assignedKeys.Remove(box.Key);
                        continue;
                    }
                    items.Add((src, box.Ox, box.Oy, box.OuterW, box.OuterH, box.InnerW, box.InnerH, box.Gutter));
                    int c0 = box.Ox / gutter, c1 = Math.Min(cols, (box.Ox + box.OuterW) / gutter);
                    for (int c = c0; c < c1; c++) skyline[c] = Math.Max(skyline[c], box.Oy + box.OuterH);
                }
                if (items.Count > 0) pageSpecs.Add((p.Width, p.Height, items, [skyline]));
            }
        }

        var remaining = inputs.Where(i => !assignedKeys.Contains(i.Key))
            .Select(i => (Source: i, Dim: Measure(i)))
            .OrderByDescending(t => t.Dim.OuterH)
            .ThenByDescending(t => t.Dim.OuterW)
            .ThenBy(t => t.Source.Key, StringComparer.Ordinal)
            .ToList();

        // First try appending unplaced items into free skyline space of existing pinned pages without changing their dimensions.
        if (pageSpecs.Count > 0 && remaining.Count > 0)
        {
            var stillUnplaced = new List<(Input Source, (int InnerW, int InnerH, int OuterW, int OuterH) Dim)>();
            foreach (var item in remaining)
            {
                bool placed = false;
                foreach (var spec in pageSpecs)
                {
                    if (TryPlaceSkyline(spec.Skylines[0], gutter, spec.Width, spec.Height, item.Dim.OuterW, item.Dim.OuterH, out int ox, out int oy))
                    {
                        spec.Items.Add((item.Source, ox, oy, item.Dim.OuterW, item.Dim.OuterH, item.Dim.InnerW, item.Dim.InnerH, gutter));
                        placed = true;
                        break;
                    }
                }
                if (!placed) stillUnplaced.Add(item);
            }
            remaining = stillUnplaced;
        }

        // Pack any remaining items into new smallest-fitting power-of-two pages (supporting half-height pages like 8192x4096).
        while (remaining.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            (int W, int H, List<(Input Source, int Ox, int Oy, int OuterW, int OuterH, int InnerW, int InnerH, int Gutter)> Placed)? chosen = null;
            int minSide = Math.Max(64, NextPot(Math.Max(remaining.Max(r => r.Dim.OuterW), gutter * 4)));
            for (int w = minSide; w <= pageSize && chosen is null; w *= 2)
            {
                foreach (int h in w >= 128 ? new[] { w / 2, w } : new[] { w })
                {
                    if (PackAllSkyline(remaining, gutter, w, h) is { } fit)
                    {
                        chosen = (w, h, fit);
                        break;
                    }
                }
            }
            if (chosen is { } single)
            {
                pageSpecs.Add((single.W, single.H, single.Placed, []));
                remaining.Clear();
            }
            else
            {
                // Fill one max-size page greedily and continue with leftovers.
                var skyline = new int[pageSize / gutter];
                var pageItems = new List<(Input Source, int Ox, int Oy, int OuterW, int OuterH, int InnerW, int InnerH, int Gutter)>();
                var leftovers = new List<(Input Source, (int InnerW, int InnerH, int OuterW, int OuterH) Dim)>();
                foreach (var item in remaining)
                {
                    if (TryPlaceSkyline(skyline, gutter, pageSize, pageSize, item.Dim.OuterW, item.Dim.OuterH, out int ox, out int oy))
                        pageItems.Add((item.Source, ox, oy, item.Dim.OuterW, item.Dim.OuterH, item.Dim.InnerW, item.Dim.InnerH, gutter));
                    else
                        leftovers.Add(item);
                }
                pageSpecs.Add((pageSize, pageSize, pageItems, []));
                remaining = leftovers;
            }
        }

        var placements = new Dictionary<string, Placement>(StringComparer.Ordinal);
        var boxes = new Dictionary<string, BoxPlacement>(StringComparer.Ordinal);
        var pages = new List<Page>(pageSpecs.Count);
        int maxShifts = gutter >= 16 ? 4 : BitOperationsLog2(gutter) + 1;
        foreach (var spec in pageSpecs)
            EncodePage(spec.Items, spec.Width, spec.Height, maxShifts, pages, placements, boxes, ct);
        return new(pages, placements, boxes);
    }

    private static List<(Input Source, int Ox, int Oy, int OuterW, int OuterH, int InnerW, int InnerH, int Gutter)>? PackAllSkyline(
        IReadOnlyList<(Input Source, (int InnerW, int InnerH, int OuterW, int OuterH) Dim)> items, int gutter, int pageW, int pageH)
    {
        var skyline = new int[pageW / gutter];
        var placed = new List<(Input Source, int Ox, int Oy, int OuterW, int OuterH, int InnerW, int InnerH, int Gutter)>(items.Count);
        foreach (var item in items)
        {
            if (!TryPlaceSkyline(skyline, gutter, pageW, pageH, item.Dim.OuterW, item.Dim.OuterH, out int ox, out int oy))
                return null;
            placed.Add((item.Source, ox, oy, item.Dim.OuterW, item.Dim.OuterH, item.Dim.InnerW, item.Dim.InnerH, gutter));
        }
        return placed;
    }

    private static bool TryPlaceSkyline(int[] skyline, int step, int pageW, int pageH, int outerW, int outerH, out int ox, out int oy)
    {
        ox = 0; oy = 0;
        if (outerW > pageW || outerH > pageH) return false;
        int wCols = outerW / step;
        int bestCol = -1, bestTop = int.MaxValue;
        for (int col = 0; col <= skyline.Length - wCols; col++)
        {
            int y = 0;
            for (int k = 0; k < wCols; k++)
                if (skyline[col + k] > y) y = skyline[col + k];
            if (y + outerH <= pageH && y + outerH < bestTop)
            {
                bestTop = y + outerH;
                bestCol = col;
            }
        }
        if (bestCol < 0) return false;
        ox = bestCol * step;
        oy = bestTop - outerH;
        for (int k = 0; k < wCols; k++) skyline[bestCol + k] = bestTop;
        return true;
    }

    private static void EncodePage(
        List<(Input Source, int Ox, int Oy, int OuterW, int OuterH, int InnerW, int InnerH, int Gutter)> packed,
        int size, int height, int maxShifts,
        List<Page> pages, Dictionary<string, Placement> placements, Dictionary<string, BoxPlacement> boxes, CancellationToken ct)
    {
        int pageIndex = pages.Count;
        var color = TextureImages.Blank(size, height);
        var normal = TextureImages.Blank(size, height, normal: true);
        var extra = packed.SelectMany(s => s.Source.Extra?.Keys ?? []).Distinct().Order().ToDictionary(slot => slot, _ => TextureImages.Blank(size, height));
        bool srgb0 = packed[0].Source.SrgbSlots?.Contains(0) == true;

        foreach (var p in packed)
        {
            ct.ThrowIfCancellationRequested();
            Fill(color, p.Source.Diffuse, p.Ox, p.Oy, p.OuterW, p.OuterH, p.InnerW, p.InnerH, p.Gutter,
                LodTextureEncoding.Channels.Color, p.Source.Alpha, p.Source.ClampMode, srgb0, p.Source.AlphaCutoff);
            Fill(normal, p.Source.Normal ?? TextureImages.Blank(4, 4, normal: true), p.Ox, p.Oy, p.OuterW, p.OuterH, p.InnerW, p.InnerH, p.Gutter,
                LodTextureEncoding.Channels.Normal, true, p.Source.ClampMode, false, 0);
            foreach (var (slot, page) in extra)
                Fill(page, p.Source.Extra![slot], p.Ox, p.Oy, p.OuterW, p.OuterH, p.InnerW, p.InnerH, p.Gutter,
                    LodTextureEncoding.Channels.Data, true, p.Source.ClampMode, p.Source.SrgbSlots?.Contains(slot) == true, 0);

            var rect = new AtlasRect(
                (p.Ox + p.Gutter + 0.5f) / size,
                (p.Oy + p.Gutter + 0.5f) / height,
                (p.Ox + p.OuterW - p.Gutter - 0.5f) / size,
                (p.Oy + p.OuterH - p.Gutter - 0.5f) / height);
            placements[p.Source.Key] = new(pageIndex, rect);
            boxes[p.Source.Key] = new(pageIndex, p.Ox, p.Oy, p.OuterW, p.OuterH, p.InnerW, p.InnerH, p.Gutter, size, height);
        }

        var diffuseMips = new List<byte[]>();
        var normalMips = new List<byte[]>();
        var extraMips = extra.Keys.ToDictionary(slot => slot, _ => new List<byte[]>());
        var targets = packed.Select(p => p.Source.Alpha ? PbrLodConverter.Coverage(ExtractRect(color, p.Ox, p.Oy, p.OuterW, p.OuterH), 1, p.Source.AlphaCutoff) : -1f).ToArray();

        for (int shift = 0; ; shift++)
        {
            ct.ThrowIfCancellationRequested();
            var shown = (byte[])color.Pixels.Clone();
            if (shift > 0)
            {
                for (int i = 0; i < packed.Count; i++)
                {
                    if (targets[i] <= 0 || targets[i] >= 0.999f) continue;
                    var p = packed[i];
                    int rx = p.Ox >> shift, ry = p.Oy >> shift, rw = Math.Max(1, p.OuterW >> shift), rh = Math.Max(1, p.OuterH >> shift);
                    var pixels = ExtractRect(color, rx, ry, rw, rh);
                    PbrLodConverter.KeepCoverage(pixels, targets[i], p.Source.AlphaCutoff, preserveMatching: true);
                    for (int y = 0; y < rh; y++)
                        pixels.AsSpan(y * rw * 4, rw * 4).CopyTo(shown.AsSpan(((ry + y) * color.Width + rx) * 4, rw * 4));
                }
            }
            diffuseMips.Add(Bc7Encoder.EncodeImage(shown, color.Width, color.Height));
            normalMips.Add(Bc7Encoder.EncodeImage(normal.Pixels, normal.Width, normal.Height));
            foreach (var (slot, page) in extra)
                extraMips[slot].Add(Bc7Encoder.EncodeImage(page.Pixels, page.Width, page.Height));

            if (shift >= maxShifts || color.Width <= 8 || color.Height <= 8) break;
            color = LodTextureEncoding.Down(color, LodTextureEncoding.Channels.Color, srgb0);
            normal = LodTextureEncoding.Down(normal, LodTextureEncoding.Channels.Normal);
            foreach (int slot in extra.Keys.ToArray())
                extra[slot] = LodTextureEncoding.Down(extra[slot], LodTextureEncoding.Channels.Data, packed[0].Source.SrgbSlots?.Contains(slot) == true);
        }

        byte[] Dds(List<byte[]> mips, int slot)
        {
            using var stream = new MemoryStream();
            DdsFile.WriteBc7(stream, size, height, mips, packed[0].Source.SrgbSlots?.Contains(slot) == true);
            return stream.ToArray();
        }
        pages.Add(new(Dds(diffuseMips, 0), Dds(normalMips, 1), size, diffuseMips.Count, extraMips.ToDictionary(k => k.Key, v => Dds(v.Value, v.Key)), height));
    }

    private static void Fill(TextureImage output, TextureImage original, int ox, int oy, int outerW, int outerH, int innerW, int innerH, int gutter,
        LodTextureEncoding.Channels channels, bool alpha, uint clamp, bool srgb, int cutoff)
    {
        float target = cutoff > 0 ? PbrLodConverter.Coverage(original.Pixels, 1, cutoff) : -1;
        var image = LodTextureEncoding.Resample(original, innerW, innerH,
            channels == LodTextureEncoding.Channels.Color && !alpha ? LodTextureEncoding.Channels.OpaqueColor : channels, srgb);
        if (!alpha)
            for (int a = 3; a < image.Pixels.Length; a += 4) image.Pixels[a] = 255;
        else if (target is > 0 and < 0.999f)
            PbrLodConverter.KeepCoverage(image.Pixels, target, cutoff, preserveMatching: true);

        for (int y = 0; y < outerH; y++)
            for (int x = 0; x < outerW; x++)
            {
                int px = x - gutter, py = y - gutter;
                px = (clamp & 2) != 0 ? (px % innerW + innerW) % innerW : Math.Clamp(px, 0, innerW - 1);
                py = (clamp & 1) != 0 ? (py % innerH + innerH) % innerH : Math.Clamp(py, 0, innerH - 1);
                image.Pixels.AsSpan((py * innerW + px) * 4, 4).CopyTo(output.Pixels.AsSpan(((oy + y) * output.Width + ox + x) * 4, 4));
            }
    }

    private static byte[] ExtractRect(TextureImage image, int rx, int ry, int rw, int rh)
    {
        var pixels = new byte[rw * rh * 4];
        for (int y = 0; y < rh; y++)
            image.Pixels.AsSpan(((ry + y) * image.Width + rx) * 4, rw * 4).CopyTo(pixels.AsSpan(y * rw * 4, rw * 4));
        return pixels;
    }

    private static int NextPot(int v)
    {
        int p = 1;
        while (p < v) p *= 2;
        return p;
    }

    private static int BitOperationsLog2(int v) => v <= 1 ? 0 : System.Numerics.BitOperations.Log2((uint)v);
}
