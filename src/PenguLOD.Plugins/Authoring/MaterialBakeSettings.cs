namespace PenguLOD.Plugins.Authoring;

/// <summary>Portable LOD calibration. This bakes a static appearance, not the game's view-dependent PBR lighting.</summary>
public sealed record MaterialBakeSettings(bool SourceNormals = true, bool PbrAlbedo = true,
    float PbrScale = .65f, float PbrGamma = 1.15f, float OcclusionStrength = .5f)
{
    public void Validate()
    {
        if (!float.IsFinite(PbrScale) || PbrScale is < .1f or > 4 || !float.IsFinite(PbrGamma) || PbrGamma is < .1f or > 4
            || !float.IsFinite(OcclusionStrength) || OcclusionStrength is < 0 or > 1)
            throw new ArgumentException("Invalid material bake calibration.");
    }
}
