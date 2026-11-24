using System.Collections.Concurrent;
using System.Numerics;
using PenguLOD.Core.Pipeline;
using PenguLOD.Core.World;
using PenguLOD.Meshes;

namespace PenguLOD.Plugins;

/// <summary>
/// Tree LOD for trees placed by light (ESL-flagged) plugins. The engine never hides <c>.btt</c> tree LOD for
/// those references (their FE-prefixed FormIDs don't match), so the LOD tree stays standing next to the real one.
/// Instead they go into the object LOD blocks as two crossed billboard cards, which the engine hides per cell
/// like any other object LOD. Trees with a 3D LOD model use the same cards beyond the levels their model is used at.
/// <para>
/// Cards for LOD8 and further (<see cref="FarPrefix"/>) take their image from one atlas of all tree billboards, so a block draws
/// all its tree cards in one shape. With every tree type on its own texture a LOD16 block had 60 such shapes. Cards at LOD4 keep
/// their own full-size textures: there are few of them per block and they are seen from close by.
/// </para>
/// </summary>
public sealed class TreeCardSource : ISyntheticMeshSource
{
    public const string Prefix = "~pengulod-treecard|";
    /// <summary>Cards for LOD8 and further, drawn from the atlas when it has their billboard.</summary>
    public const string FarPrefix = Prefix + "far|";
    /// <summary>The atlas textures, as the LOD meshes name them (written into the output folder).</summary>
    public const string AtlasDiffuse = "textures\\pengulod\\trees\\cards.dds", AtlasNormal = "textures\\pengulod\\trees\\cards_n.dds";
    /// <summary>Near (LOD4) tree-card atlas textures for ESL-placed and billboard-fallback trees.</summary>
    public const string NearAtlasDiffuse = "textures\\pengulod\\trees\\cards_lod4.dds", NearAtlasNormal = "textures\\pengulod\\trees\\cards_lod4_n.dds";
    /// <summary>Largest side a billboard keeps in the far atlas. A 1,500-unit tree is about 45 pixels tall on a 4K screen where LOD8 begins.</summary>
    public const int AtlasTile = 128;
    /// <summary>Largest side a billboard keeps in the near (LOD4) atlas.</summary>
    public const int NearAtlasTile = 512;
    private const int Version = 5; // near and far card atlases

    /// <summary>The mesh path of a tree's card: near card at LOD4, far atlas from LOD8 on.</summary>
    public static string CardPath(string billboardTexture, bool far) => (far ? FarPrefix : Prefix) + billboardTexture;

    private static (string Texture, bool Far) Split(string path)
        => path.StartsWith(FarPrefix, StringComparison.Ordinal) ? (path[FarPrefix.Length..], true) : (path[Prefix.Length..], false);

    private readonly AssetIndex _assets;
    private readonly float _brightness;
    private readonly ConcurrentDictionary<string, LodMesh?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, AtlasRect>? _atlasRects, _nearAtlasRects;
    private string _atlasHash = "-", _nearAtlasHash = "-";

    /// <summary>What the far atlas holds, for the log (null until <see cref="PrepareAtlas"/> has built one).</summary>
    public TreeCardAtlasSummary? Atlas { get; private set; }

    /// <summary>What the near (LOD4) atlas holds, for the log (null until <see cref="PrepareNearAtlas"/> has built one).</summary>
    public TreeCardAtlasSummary? NearAtlas { get; private set; }

    /// <summary>
    /// Packs the front and side billboards of the given trees into <see cref="AtlasDiffuse"/> and a normal-map atlas with the same
    /// layout, in the output folder. Must run before blocks are built: far cards take their UVs from it. A billboard that is not in
    /// it (an unreadable file) keeps its own texture.
    /// </summary>
    public void PrepareAtlas(string outputFolder, IEnumerable<string> billboardTextures)
    {
        if (_atlasRects is not null) return;
        if (BuildBillboardAtlas(outputFolder, billboardTextures, AtlasTile, AtlasDiffuse, AtlasNormal) is not { } atlas) return;
        _atlasRects = atlas.Rects; _atlasHash = atlas.Hash;
        Atlas = new(atlas.Rects.Count, atlas.Size, atlas.SizeCap, atlas.Reduced, atlas.Skipped);
        _cache.Clear();
    }

    /// <summary>
    /// Packs near (LOD4) tree cards (ESL-placed trees and 3D-tree billboard fallbacks) into <see cref="NearAtlasDiffuse"/> and
    /// <see cref="NearAtlasNormal"/> at up to <see cref="NearAtlasTile"/> px per billboard, collapsing per-tree LOD4 shapes into a single draw call.
    /// </summary>
    public void PrepareNearAtlas(string outputFolder, IEnumerable<string> billboardTextures)
    {
        if (_nearAtlasRects is not null) return;
        if (BuildBillboardAtlas(outputFolder, billboardTextures, NearAtlasTile, NearAtlasDiffuse, NearAtlasNormal) is not { } atlas) return;
        _nearAtlasRects = atlas.Rects; _nearAtlasHash = atlas.Hash;
        NearAtlas = new(atlas.Rects.Count, atlas.Size, atlas.SizeCap, atlas.Reduced, atlas.Skipped);
        _cache.Clear();
    }

    private BillboardAtlas.Result? BuildBillboardAtlas(string outputFolder, IEnumerable<string> billboardTextures, int maxTile, string diffusePath, string normalPath)
    {
        var tiles = new List<BillboardAtlas.Tile>();
        foreach (string plain in billboardTextures.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            var (dds, normal, txt) = Files(plain);
            if (!_assets.Exists(dds)) continue;
            tiles.Add(new(GamePath.Normalize(dds), dds, _assets.Exists(normal) ? normal : null));
            string side = txt[..^4] + "_2.dds", sideNormal = txt[..^4] + "_2_n.dds";
            if (_assets.Exists(side)) tiles.Add(new(GamePath.Normalize(side), side, _assets.Exists(sideNormal) ? sideNormal : null));
        }
        byte[]? Read(string texture)
        {
            if (!_assets.TryOpen(texture, out var stream)) return null;
            using (stream) { using var buffer = new MemoryStream(); stream.CopyTo(buffer); return buffer.ToArray(); }
        }
        if (BillboardAtlas.Build(tiles, Read, maxTile) is not { } atlas) return null;
        BillboardAtlas.Write(atlas, outputFolder, diffusePath, normalPath);
        return atlas;
    }

    /// <param name="brightness">Tree LOD brightness (1 = as the billboard is). Applied through vertex colours.</param>
    public TreeCardSource(AssetIndex assets, float brightness = 1f)
    {
        _assets = assets;
        _brightness = brightness;
    }

    public static bool NeedsCards(TreeReference t) => t.ObjectLod || (t.RuntimeFormId >> 24) == 0xFE;

    /// <summary>LOD references for the given trees: LOD4 and LOD8 blocks, z-rotation and scale from the tree.</summary>
    public IEnumerable<LodReference> ToReferences(IEnumerable<TreeReference> trees)
    {
        foreach (var t in trees)
        {
            yield return new LodReference(
                FormKey: t.FormKey,
                BaseFormKey: t.BaseFormKey ?? "tree",
                BaseEditorId: "Tree LOD card",
                Worldspace: t.Worldspace,
                WinningPlugin: "tree LOD",
                Position: t.Position,
                RotationRadians: new Vector3(0, 0, t.RotationZ),
                Scale: t.Scale,
                Meshes: new LodMeshSet(CardPath(t.Billboard.TexturePath, far: false), CardPath(t.Billboard.TexturePath, far: true), null, null),
                Flags: LodReferenceFlags.None);
        }
    }

    public bool Owns(string path) => path.StartsWith(Prefix, StringComparison.Ordinal);

    public string? Fingerprint(string path)
    {
        if (!Owns(path)) return null;
        var (texture, far) = Split(path);
        var (dds, normal, txt) = Files(texture);
        var side = txt[..^4] + "_2";
        string placement = far ? "atlas:" + _atlasHash : _nearAtlasRects is not null ? "near:" + _nearAtlasHash : "own";
        return $"c{Version}|{placement}|b{_brightness:F3}|{_assets.Fingerprint(dds)}|{_assets.Fingerprint(normal)}|{_assets.Fingerprint(txt)}|{_assets.Fingerprint(side + ".dds")}|{_assets.Fingerprint(side + "_n.dds")}";
    }

    public LodMesh? Build(string path) => _cache.GetOrAdd(path, p => { var (texture, far) = Split(p); return Create(texture, far); });

    /// <summary>Prefers the front view (<c>_1.dds</c> + <c>_1_n.dds</c>), which TexGen renders with a normal map.</summary>
    private (string Dds, string Normal, string Txt) Files(string plainDds)
    {
        var stem = plainDds.EndsWith("_1.dds", StringComparison.OrdinalIgnoreCase) ? plainDds[..^6] : plainDds[..^4];
        return _assets.Exists(stem + "_1.dds") ? (stem + "_1.dds", stem + "_1_n.dds", stem + ".txt")
            : (stem + ".dds", stem + "_n.dds", stem + ".txt");
    }

    /// <summary>The atlas rectangle and texture pair of a billboard texture when the active near/far atlas holds it.</summary>
    private (AtlasRect Rect, string Diffuse, string Normal)? Rect(string dds, bool far)
    {
        var key = GamePath.Normalize(dds);
        if (far)
            return _atlasRects is not null && _atlasRects.TryGetValue(key, out var farRect) ? (farRect, AtlasDiffuse, AtlasNormal) : null;
        return _nearAtlasRects is not null && _nearAtlasRects.TryGetValue(key, out var nearRect) ? (nearRect, NearAtlasDiffuse, NearAtlasNormal) : null;
    }

    private LodMesh? Create(string plainDds, bool far)
    {
        string path = CardPath(plainDds, far);
        var (dds, normal, txt) = Files(plainDds);
        if (!_assets.Exists(dds) || !_assets.TryOpen(txt, out var st)) return null;
        string text;
        using (var reader = new StreamReader(st)) text = reader.ReadToEnd();
        var bb = TreeBillboard.Parse(dds, text, useFirstView: dds.EndsWith("_1.dds", StringComparison.OrdinalIgnoreCase));
        if (bb is null) return null;

        string sideDds = txt[..^4] + "_2.dds", sideNormal = txt[..^4] + "_2_n.dds";
        if (_assets.Exists(sideDds) && TreeBillboard.Parse(sideDds, text, view: 2) is { } side)
        {
            // Both views from the atlas, or neither: a card pair on two materials would be two shapes again.
            var frontRect = Rect(dds, far); var sideRect = Rect(sideDds, far);
            if (frontRect is null || sideRect is null) frontRect = sideRect = null;
            return new LodMesh { Path = path, Parts = [ViewCard(bb, normal, false, frontRect), ViewCard(side, sideNormal, true, sideRect)] };
        }

        var rect = Rect(dds, far);
        var (u0, v0, u1, v1) = rect is { } r ? (r.Rect.U0, r.Rect.V0, r.Rect.U1, r.Rect.V1) : (0f, 0f, 1f, 1f);
        var material = new LodMaterial(
            rect is null ? [dds, _assets.Exists(normal) ? normal : "", "", "", "", "", "", "", ""] : [rect.Value.Diffuse, rect.Value.Normal, "", "", "", "", "", "", ""],
            0x8000_0300u,
            0x0000_0005u | 0x10u, // double-sided
            3,
            true, GrassPatchBuilder.AlphaTestFlags, GrassPatchBuilder.AlphaThreshold,
            Vector3.Zero, 1f);

        // Two upright cards crossing at the trunk, bottom at ShiftZ (scaled with the tree by the reference transform).
        float hw = bb.Width * 0.5f, z0 = bb.ShiftZ, z1 = bb.ShiftZ + bb.Height;
        var pos = new Vector3[8];
        var uv = new Vector2[8];
        var nrm = new Vector3[8];
        var tan = new Vector3[8];
        var bit = new Vector3[8];
        var tris = new ushort[12];
        for (int c = 0; c < 2; c++)
        {
            var dir = c == 0 ? Vector3.UnitX : Vector3.UnitY;
            var normalDir = c == 0 ? -Vector3.UnitY : Vector3.UnitX;
            int v = c * 4;
            pos[v] = -dir * hw + new Vector3(0, 0, z0);
            pos[v + 1] = dir * hw + new Vector3(0, 0, z0);
            pos[v + 2] = dir * hw + new Vector3(0, 0, z1);
            pos[v + 3] = -dir * hw + new Vector3(0, 0, z1);
            uv[v] = new Vector2(u0, v1); uv[v + 1] = new Vector2(u1, v1); uv[v + 2] = new Vector2(u1, v0); uv[v + 3] = new Vector2(u0, v0);
            // NIF tangent frame: tangent along +V (down the card), bitangent along +U (across it).
            for (int k = 0; k < 4; k++) { nrm[v + k] = normalDir; tan[v + k] = -Vector3.UnitZ; bit[v + k] = dir; }
            int t = c * 6;
            tris[t] = (ushort)v; tris[t + 1] = (ushort)(v + 1); tris[t + 2] = (ushort)(v + 2);
            tris[t + 3] = (ushort)v; tris[t + 4] = (ushort)(v + 2); tris[t + 5] = (ushort)(v + 3);
        }
        uint[]? colors = null;
        if (MathF.Abs(_brightness - 1f) > 0.001f)
        {
            var c = BtoBuilder.Scale(0xFFFF_FFFFu, _brightness);
            colors = new uint[pos.Length];
            Array.Fill(colors, c);
        }
        return new LodMesh
        {
            Path = path,
            Parts = [new LodMeshPart { Material = material, Positions = pos, UVs = uv, Normals = nrm, Tangents = tan, Bitangents = bit, Colors = colors, Triangles = tris }],
        };
    }

    private LodMeshPart ViewCard(TreeBillboard bb, string normal, bool side, (AtlasRect Rect, string Diffuse, string Normal)? rect)
    {
        var (u0, v0, u1, v1) = rect is { } r ? (r.Rect.U0, r.Rect.V0, r.Rect.U1, r.Rect.V1) : (0f, 0f, 1f, 1f);
        var dir = side ? Vector3.UnitY : Vector3.UnitX;
        var normalDir = side ? Vector3.UnitX : -Vector3.UnitY;
        float half = bb.Width * .5f;
        var bottom = new Vector3(0, 0, bb.ShiftZ); var top = bottom + Vector3.UnitZ * bb.Height;
        return new LodMeshPart
        {
            Name = side ? "Side billboard" : "Front billboard",
            Material = new LodMaterial(rect is null ? [bb.TexturePath, _assets.Exists(normal) ? normal : "", "", "", "", "", "", "", ""] : [rect.Value.Diffuse, rect.Value.Normal, "", "", "", "", "", "", ""],
                0x8000_0300u, 0x0000_0015u, 3, true, GrassPatchBuilder.AlphaTestFlags, GrassPatchBuilder.AlphaThreshold, Vector3.Zero, 1),
            Positions = [bottom - dir * half, bottom + dir * half, top + dir * half, top - dir * half],
            UVs = [new(u0, v1), new(u1, v1), new(u1, v0), new(u0, v0)],
            Normals = [normalDir, normalDir, normalDir, normalDir], Tangents = [-Vector3.UnitZ, -Vector3.UnitZ, -Vector3.UnitZ, -Vector3.UnitZ],
            Bitangents = [dir, dir, dir, dir], Triangles = [0, 1, 2, 0, 2, 3],
            Colors = MathF.Abs(_brightness - 1) <= .001f ? null : Enumerable.Repeat(BtoBuilder.Scale(0xFFFFFFFF, _brightness), 4).ToArray()
        };
    }
}

/// <summary>The tree card atlas for the log: tiles, side length in pixels, the tile size cap that was needed, tiles scaled down, billboards that couldn't be read.</summary>
public sealed record TreeCardAtlasSummary(int Tiles, int Size, int SizeCap, int Reduced, int Skipped);

/// <summary>Several synthetic mesh sources behind one interface.</summary>
public sealed class CompositeSyntheticSource(params ISyntheticMeshSource?[] sources) : ISyntheticMeshSource
{
    private readonly ISyntheticMeshSource[] _sources = sources.Where(s => s is not null).ToArray()!;

    public bool Owns(string path) => _sources.Any(s => s.Owns(path));

    public LodMesh? Build(string path) => _sources.FirstOrDefault(s => s.Owns(path))?.Build(path);

    public bool MapTextures(string path) => _sources.FirstOrDefault(s => s.Owns(path))?.MapTextures(path) ?? false;
}
