using System.Numerics;
using System.Text.Json;
using PenguLOD.Core.Pipeline;

namespace PenguLOD.Core.Lod;

public sealed record TruePbrRule(string MatchDiffuse, Vector3 ColorScale, float RoughnessScale = 1f, float RoughnessOffset = 0f, float SpecularLevel = 0.04f);

/// <summary>
/// Indexes Community Shaders / PGPatcher <c>PBRJSONs\**\*.json</c> material definitions across the virtual Data view,
/// resolving albedo colour scales and roughness scale/offset adjustments for PBR LOD baking.
/// </summary>
public sealed class TruePbrCatalog
{
    private readonly List<TruePbrRule> _rules = [];

    public int Count => _rules.Count;

    public static TruePbrCatalog Load(IAssetSource assets, IEnumerable<string>? jsonPaths)
    {
        var catalog = new TruePbrCatalog();
        if (jsonPaths is null) return catalog;
        foreach (var path in jsonPaths.Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!assets.TryOpen(path, out var stream)) continue;
            try
            {
                using (stream)
                using (var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }))
                {
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var elem in doc.RootElement.EnumerateArray())
                            catalog.TryAddElement(elem);
                    }
                    else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        catalog.TryAddElement(doc.RootElement);
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or FormatException) { }
        }
        return catalog;
    }

    private void TryAddElement(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object) return;
        if (TryBool(obj, "delete") == true || TryBool(obj, "pbr") == false) return;

        string? match = TryString(obj, "match_diffuse") ?? TryString(obj, "texture") ?? TryString(obj, "diffuse");
        if (string.IsNullOrWhiteSpace(match)) return;
        match = NormalizeToken(match);
        if (match.Length == 0) return;

        Vector3 colorScale = Vector3.One;
        if (obj.TryGetProperty("base_color_scale", out var bcs) || obj.TryGetProperty("color_scale", out bcs))
        {
            if (bcs.ValueKind == JsonValueKind.Number && bcs.TryGetSingle(out float scalar) && float.IsFinite(scalar))
                colorScale = new Vector3(Math.Clamp(scalar, 0f, 4f));
            else if (bcs.ValueKind == JsonValueKind.Array)
            {
                var arr = bcs.EnumerateArray().ToArray();
                if (arr.Length >= 3 && arr[0].TryGetSingle(out float r) && arr[1].TryGetSingle(out float g) && arr[2].TryGetSingle(out float b)
                    && float.IsFinite(r) && float.IsFinite(g) && float.IsFinite(b))
                    colorScale = new Vector3(Math.Clamp(r, 0f, 4f), Math.Clamp(g, 0f, 4f), Math.Clamp(b, 0f, 4f));
            }
        }

        float roughScale = TryFloat(obj, "roughness_scale") ?? 1f;
        float roughOffset = TryFloat(obj, "roughness_offset") ?? 0f;
        float specLevel = TryFloat(obj, "specular_level") ?? 0.04f;
        _rules.Add(new(match, colorScale, Math.Clamp(roughScale, 0f, 4f), Math.Clamp(roughOffset, -1f, 1f), Math.Clamp(specLevel, 0f, 1f)));
    }

    public TruePbrRule? Match(string diffusePath)
    {
        if (_rules.Count == 0 || string.IsNullOrWhiteSpace(diffusePath)) return null;
        string norm = NormalizeToken(diffusePath);
        if (norm.EndsWith(".dds", StringComparison.Ordinal)) norm = norm[..^4];
        TruePbrRule? best = null;
        for (int i = 0; i < _rules.Count; i++)
        {
            var r = _rules[i];
            if (norm.Contains(r.MatchDiffuse, StringComparison.Ordinal))
            {
                // Later files in load order win; among equal priority, prefer more specific match strings.
                if (best is null || r.MatchDiffuse.Length >= best.MatchDiffuse.Length)
                    best = r;
            }
        }
        return best;
    }

    private static string NormalizeToken(string s)
    {
        var p = s.Replace('/', '\\').Trim().TrimStart('\\').ToLowerInvariant();
        if (p.StartsWith("data\\", StringComparison.Ordinal)) p = p[5..];
        if (p.StartsWith("textures\\", StringComparison.Ordinal)) p = p[9..];
        if (p.StartsWith("pbr\\", StringComparison.Ordinal)) p = p[4..];
        if (p.EndsWith(".dds", StringComparison.Ordinal)) p = p[..^4];
        return p;
    }

    private static string? TryString(JsonElement obj, string prop)
    {
        foreach (var p in obj.EnumerateObject())
            if (p.Name.Equals(prop, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                return p.Value.GetString();
        return null;
    }

    private static bool? TryBool(JsonElement obj, string prop)
    {
        foreach (var p in obj.EnumerateObject())
            if (p.Name.Equals(prop, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return p.Value.GetBoolean();
        return null;
    }

    private static float? TryFloat(JsonElement obj, string prop)
    {
        foreach (var p in obj.EnumerateObject())
            if (p.Name.Equals(prop, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetSingle(out float f) && float.IsFinite(f))
                return f;
        return null;
    }
}
