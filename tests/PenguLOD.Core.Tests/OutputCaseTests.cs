using PenguLOD.Core.Pipeline;
using PenguLOD.Core.World;
using PenguLOD.Plugins;
using Xunit;

namespace PenguLOD.Core.Tests;

/// <summary>
/// One spelling per output path. Two names that differ only in case can only exist side by side on a case-sensitive file system, so
/// the tests that need them do nothing elsewhere.
/// </summary>
public sealed class OutputCaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pengulod-case-tests-" + Guid.NewGuid().ToString("N"));
    private readonly bool _caseSensitive;

    public OutputCaseTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "probe"));
        _caseSensitive = !Directory.Exists(Path.Combine(_root, "PROBE"));
        Directory.Delete(Path.Combine(_root, "probe"));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private string Put(string path, string text = "x", DateTime? written = null)
    {
        string file = Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        if (written is { } time) File.SetLastWriteTimeUtc(file, time);
        return file;
    }

    private string[] Files() => Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
        .Select(p => Path.GetRelativePath(_root, p).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void An_output_with_one_spelling_is_left_alone()
    {
        Put("meshes/terrain/Tamriel/objects/Tamriel.4.0.0.bto");
        Put("textures/lod/a.dds");
        Assert.Empty(OutputCase.Conflicts(_root));
        Assert.Empty(OutputCase.Unify(_root));
        Assert.Equal(["meshes/terrain/Tamriel/objects/Tamriel.4.0.0.bto", "textures/lod/a.dds"], Files());
        Assert.Empty(OutputCase.Conflicts(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void Folders_that_differ_only_in_case_are_merged_into_the_lower_case_one()
    {
        if (!_caseSensitive) return;
        // What 0.6.0-beta.2 wrote: the underside mesh and two stand-in textures beside the blocks, under other spellings.
        Put("meshes/terrain/Tamriel/objects/Tamriel.4.0.0.bto");
        Put("meshes/Terrain/Tamriel/Tamriel_Underside.nif");
        Put("textures/enderal/lod/castlewalls01lod.dds");
        Put("textures/Enderal/LOD/castlewalls03lod.dds");
        Put("Textures/NercoVamp/Castle/T_WindowGlass_N.ddsn");

        Assert.Equal([["Textures", "textures"], ["meshes/Terrain", "meshes/terrain"], ["textures/Enderal", "textures/enderal"]],
            OutputCase.Conflicts(_root).Select(g => g.ToArray()).ToArray());
        Assert.Equal(["Textures", "meshes/Terrain", "textures/Enderal"], OutputCase.Unify(_root).Order(StringComparer.Ordinal).ToArray());
        Assert.Empty(OutputCase.Conflicts(_root));
        Assert.Equal([
            "meshes/terrain/Tamriel/Tamriel_Underside.nif", "meshes/terrain/Tamriel/objects/Tamriel.4.0.0.bto",
            "textures/NercoVamp/Castle/T_WindowGlass_N.ddsn", "textures/enderal/lod/castlewalls01lod.dds", "textures/enderal/lod/castlewalls03lod.dds",
        ], Files());
    }

    [Fact]
    public void Of_two_files_the_one_written_last_is_kept_and_without_a_lower_case_name_the_first_spelling_wins()
    {
        if (!_caseSensitive) return;
        var old = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Put("textures/lod/Wall.dds", "old", old);
        Put("textures/LOD/wall.dds", "new", old.AddDays(1));
        Put("meshes/Alpha/a.nif");
        Put("meshes/ALPHA/b.nif");

        OutputCase.Unify(_root);
        Assert.Equal(["meshes/ALPHA/a.nif", "meshes/ALPHA/b.nif", "textures/lod/Wall.dds"], Files());
        Assert.Equal("new", File.ReadAllText(Path.Combine(_root, "textures", "lod", "Wall.dds")));
    }

    [Fact]
    public void A_file_and_a_folder_under_the_same_name_stop_the_merge()
    {
        if (!_caseSensitive) return;
        Put("textures/EC");
        Put("textures/ec/a.dds");
        Assert.Throws<IOException>(() => OutputCase.Unify(_root));
    }

    [Fact]
    public void Recording_a_stage_output_merges_first_so_every_file_has_one_recorded_path()
    {
        Put("meshes/terrain/World/objects/World.4.0.0.bto");
        if (_caseSensitive) Put("meshes/Terrain/World/World_Underside.nif");
        var lines = new List<string>();
        var snapshot = OutputSnapshot.Capture(_root, new Collect(lines));
        Assert.All(snapshot.Files.Keys, k => Assert.StartsWith("meshes/terrain/World/", k));
        Assert.Equal(snapshot.Files.Count, snapshot.Files.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(_caseSensitive, lines.Any(l => l.Contains("differed only in case") && l.Contains("meshes/Terrain")));
    }

    [Fact]
    public void Generated_paths_use_one_spelling_at_the_source()
    {
        Assert.Equal(@"meshes\terrain\Tamriel\Tamriel_Underside.nif", PenguLOD.Meshes.UndersideBuilder.RelativePath("Tamriel"));
        Assert.StartsWith(@"meshes\terrain\Tamriel\", new QuadKey("Tamriel", LodLevel.Lod4, 0, 0).RelativePath);

        // A stand-in for a missing texture goes to the lower-case path, whatever spelling the mesh used.
        StubTextures.Write(_root, @"Textures\Enderal\LOD\CastleWalls03LOD.dds");
        StubTextures.Write(_root, @"textures\enderal\lod\castlewalls01lod_n.dds");
        Assert.Equal(["textures/enderal/lod/castlewalls01lod_n.dds", "textures/enderal/lod/castlewalls03lod.dds"], Files());
    }

    [Fact]
    public void Removing_underside_files_also_removes_those_under_the_earlier_spelling()
    {
        if (!_caseSensitive) return;
        Put("meshes/terrain/World/objects/World.4.0.0.bto");
        Put("meshes/Terrain/World/World_Underside.nif");
        Put("meshes/terrain/Other/Other_Underside.nif");
        UndersideStage.RemoveAll(_root);
        Assert.Equal(["meshes/terrain/World/objects/World.4.0.0.bto"], Files());
        Assert.False(Directory.Exists(Path.Combine(_root, "meshes", "Terrain")));
    }

    [Fact]
    public void Verifying_an_output_reports_names_that_differ_only_in_case()
    {
        Put("meshes/terrain/World/objects/World.4.0.0.bto");
        OutputSetReceipt.Record(_root, "lod", "source", OutputSnapshot.Capture(_root));
        Assert.DoesNotContain(OutputManagement.Verify([_root]).Findings, f => f.Code == "case-conflict");
        if (!_caseSensitive) return;
        Put("meshes/Terrain/World/World_Underside.nif");
        var finding = Assert.Single(OutputManagement.Verify([_root]).Findings, f => f.Code == "case-conflict");
        Assert.Equal("Warning", finding.Severity);
        Assert.Contains("meshes/Terrain, meshes/terrain", finding.Message);
    }

    private sealed class Collect(List<string> lines) : IProgress<string> { public void Report(string value) => lines.Add(value); }
}
