using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using PenguLOD.Core.Lod;
using PenguLOD.Core.Pipeline;
using PenguLOD.Core.World;
using PenguLOD.Textures;

namespace PenguLOD.Plugins;

/// <summary>
/// Writes converted PBR/Complex Material albedo and gloss-normal copies that object LOD points at (<see cref="PbrLodTextures.OutputRoot"/>).
/// </summary>
public static class PbrLodStage
{
    private const string Sidecar = "pbrlod.json";

    public sealed record Result(int Converted, int Unchanged, int Failed, int Twins, TimeSpan Elapsed, IReadOnlyList<string> Errors);

    public static Result Run(IAssetSource assets, PbrLodTextures pbr, string output, int maxSize, float brightness,
        IProgress<string>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var root = GamePath.ToFileSystemPath(output, PbrLodTextures.OutputRoot);
        var sidecarPath = Path.Combine(root, Sidecar);
        var known = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(sidecarPath) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(sidecarPath)) is { } old)
                foreach (var kv in old) known[kv.Key] = kv.Value;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }

        var settings = FormattableString.Invariant($"{maxSize}|{brightness:F3}|{PbrLodConverter.Gamma}|{PbrLodConverter.BaseScale}|m{PbrLodConverter.MipVersion}");
        int converted = 0, unchanged = 0, failed = 0;
        var errors = new ConcurrentBag<string>();

        byte[]? TryRead(string? path)
        {
            if (path is null || !assets.TryOpen(path, out var s)) return null;
            using (s) { using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray(); }
        }

        Parallel.ForEach(pbr.Conversions, RunLimits.Parallel(ct), kv =>
        {
            var (rel, src) = (kv.Key, kv.Value);
            var dest = GamePath.ToFileSystemPath(output, rel);
            var (rmaosPath, maskPath) = PbrLodTextures.FindCompanionMaps(assets, src);
            var rule = pbr.Catalog?.Match(src);
            var ruleStamp = rule is null ? "-" : FormattableString.Invariant($"{rule.ColorScale.X:R},{rule.ColorScale.Y:R},{rule.ColorScale.Z:R}:{rule.RoughnessScale:R}:{rule.RoughnessOffset:R}");
            var stamp = $"{settings}|{assets.Fingerprint(src) ?? "?"}|{assets.Fingerprint(rmaosPath ?? "") ?? "-"}|{assets.Fingerprint(maskPath ?? "") ?? "-"}|{ruleStamp}";
            if (File.Exists(dest) && known.TryGetValue(rel, out var prev) && prev == stamp) { Interlocked.Increment(ref unchanged); return; }
            try
            {
                var bytes = TryRead(src) ?? throw new FileNotFoundException(src);
                var dds = PbrLodConverter.Convert(bytes, maxSize, brightness, TryRead(rmaosPath), TryRead(maskPath), rule?.ColorScale);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.WriteAllBytes(dest, dds);
                known[rel] = stamp;
                Interlocked.Increment(ref converted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref failed);
                errors.Add($"{src}: {ex.Message}");
            }
        });

        Parallel.ForEach(pbr.NormalConversions, RunLimits.Parallel(ct), kv =>
        {
            var (rel, spec) = (kv.Key, kv.Value);
            var dest = GamePath.ToFileSystemPath(output, rel);
            var rule = pbr.Catalog?.Match(spec.NormalSource[..^6] + ".dds");
            var ruleStamp = rule is null ? "-" : FormattableString.Invariant($"{rule.RoughnessScale:R}:{rule.RoughnessOffset:R}");
            var stamp = $"n|{maxSize}|m{PbrLodConverter.MipVersion}|{assets.Fingerprint(spec.NormalSource) ?? "?"}|{assets.Fingerprint(spec.RmaosSource ?? "") ?? "-"}|{assets.Fingerprint(spec.ComplexMaskSource ?? "") ?? "-"}|{ruleStamp}";
            if (File.Exists(dest) && known.TryGetValue(rel, out var prev) && prev == stamp) { Interlocked.Increment(ref unchanged); return; }
            try
            {
                var bytes = TryRead(spec.NormalSource) ?? throw new FileNotFoundException(spec.NormalSource);
                var dds = PbrLodConverter.ConvertNormalWithGloss(bytes, maxSize, TryRead(spec.RmaosSource), TryRead(spec.ComplexMaskSource),
                    rule?.RoughnessScale ?? 1f, rule?.RoughnessOffset ?? 0f);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.WriteAllBytes(dest, dds);
                known[rel] = stamp;
                Interlocked.Increment(ref converted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref failed);
                errors.Add($"{spec.NormalSource}: {ex.Message}");
            }
        });

        if (known.Count > 0)
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(sidecarPath, JsonSerializer.Serialize(known.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(k => k.Key, k => k.Value)));
        }
        return new Result(converted, unchanged, failed, pbr.TwinsUsed, sw.Elapsed, [.. errors.Take(20)]);
    }

    public static void RemoveAll(string output)
    {
        try
        {
            var root = GamePath.ToFileSystemPath(output, PbrLodTextures.OutputRoot);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
