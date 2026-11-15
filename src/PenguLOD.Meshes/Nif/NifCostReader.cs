using PenguLOD.Core.Lod;

namespace PenguLOD.Meshes.Nif;

public sealed record GeometryCost(string Layer, long Triangles, long Vertices, int MeshGroups);

/// <summary>
/// One drawn shape of a block: its layer and name, size, shader flags and first texture (lower case, empty when the shader is not a
/// lighting shader or names none). A shape is a draw call; shapes with the same texture and flags could have been one.
/// </summary>
public sealed record ShapeCost(string Layer, string Name, int Triangles, int Vertices, uint ShaderFlags1, uint ShaderFlags2, string Texture);

/// <summary>Counts reachable drawable geometry without decoding vertex buffers or textures.</summary>
public static class NifCostReader
{
    public static IReadOnlyList<GeometryCost> Read(byte[] bytes, string defaultLayer = LodLayers.Objects)
    {
        try { return ReadCore(bytes, defaultLayer); }
        catch (Exception e) when (e is OverflowException or ArgumentOutOfRangeException or IndexOutOfRangeException or EndOfStreamException)
        { throw new InvalidDataException("Malformed NIF: " + e.Message, e); }
    }

    /// <summary>Every visible shape with geometry, in scene order. Reads headers and shader blocks only.</summary>
    public static IReadOnlyList<ShapeCost> ReadShapes(byte[] bytes, string defaultLayer = LodLayers.Objects)
    {
        var shapes = new List<ShapeCost>();
        try { ReadCore(bytes, defaultLayer, shapes); }
        catch (Exception e) when (e is OverflowException or ArgumentOutOfRangeException or IndexOutOfRangeException or EndOfStreamException)
        { throw new InvalidDataException("Malformed NIF: " + e.Message, e); }
        return shapes;
    }

    /// <summary>Shader flags and first texture of a lighting shader block; zeros and "" for anything else or a damaged block.</summary>
    private static (uint Flags1, uint Flags2, string Texture) Shader(NifFile nif, int shaderRef)
    {
        if (shaderRef < 0 || shaderRef >= nif.Blocks.Count || nif.Blocks[shaderRef].Type != "BSLightingShaderProperty") return (0, 0, "");
        try
        {
            var r = nif.ReaderFor(nif.Blocks[shaderRef]);
            r.U32();                                        // shader type
            r.I32();                                        // name
            r.Skip(checked((int)r.U32() * 4));              // extra data
            r.I32();                                        // controller
            uint f1 = r.U32(), f2 = r.U32();
            r.Skip(16);                                     // UV offset and scale
            int textureSet = r.I32();
            string texture = "";
            if (textureSet >= 0 && textureSet < nif.Blocks.Count && nif.Blocks[textureSet].Type == "BSShaderTextureSet")
            {
                var t = nif.ReaderFor(nif.Blocks[textureSet]);
                if (t.U32() > 0) texture = t.SizedString().Replace('/', '\\').ToLowerInvariant();
            }
            return (f1, f2, texture);
        }
        catch (Exception e) when (e is OverflowException or ArgumentOutOfRangeException or IndexOutOfRangeException or EndOfStreamException or InvalidDataException)
        { return (0, 0, ""); }
    }

    private static IReadOnlyList<GeometryCost> ReadCore(byte[] bytes, string defaultLayer, List<ShapeCost>? shapes = null)
    {
        var nif = NifFile.Read(bytes);
        if (nif.BsVersion != NifFile.BsVersionSse) throw new NotSupportedException("Geometry costs require an SSE NIF.");
        var costs = new Dictionary<string, GeometryCost>(StringComparer.Ordinal);
        var visited = new HashSet<int>();
        var pending = new Stack<(int Index, string Layer, bool Hidden)>();
        foreach (int root in nif.Roots) pending.Push((root, defaultLayer, false));
        while (pending.TryPop(out var item))
        {
            if (item.Index == -1) continue;
            if (item.Index < 0 || item.Index >= nif.Blocks.Count) throw new InvalidDataException("Invalid scene reference.");
            if (!visited.Add(item.Index)) throw new InvalidDataException("Repeated or cyclic scene reference; drawable counts are ambiguous.");
            var block = nif.Blocks[item.Index];
            bool node = block.Type is "NiNode" or "BSMultiBoundNode" or "BSFadeNode";
            bool shape = block.Type is "BSTriShape" or "BSSubIndexTriShape";
            if (!node && !shape) throw new NotSupportedException("Unmeasured scene block: " + block.Type);
            var reader = nif.ReaderFor(block);
            string? name = nif.String(reader.I32());
            reader.Skip(checked((int)reader.U32() * 4)); // extra data
            reader.I32(); // controller
            uint flags = reader.U32();
            reader.Skip(56); // transform and collision link
            bool hidden = item.Hidden || (flags & 1) != 0;
            string layer = item.Layer;
            if (name is not null && (name.StartsWith(LodLayers.NodePrefix, StringComparison.Ordinal) || name.StartsWith("AL:", StringComparison.Ordinal)))
            {
                string candidate = name.Split(':')[1];
                layer = LodLayers.All.Contains(candidate) ? candidate : "other";
            }
            if (node)
            {
                uint count = reader.U32(); reader.Require(checked((int)count * 4));
                for (int i = 0; i < count; i++) pending.Push((reader.I32(), layer, hidden));
            }
            else
            {
                reader.Skip(20); // bound, skin
                int shaderRef = reader.I32();
                reader.Skip(4);  // alpha
                ulong descriptor = reader.U64();
                int triangles = reader.U16(), vertices = reader.U16();
                uint size = reader.U32();
                long required = (long)(descriptor & 15) * 4 * vertices + triangles * 6L;
                if (size < required || size > reader.Remaining) throw new InvalidDataException("Truncated geometry buffer.");
                if (hidden || triangles == 0 || vertices == 0) continue;
                var old = costs.GetValueOrDefault(layer) ?? new(layer, 0, 0, 0);
                costs[layer] = new(layer, old.Triangles + triangles, old.Vertices + vertices, old.MeshGroups + 1);
                if (shapes is not null)
                {
                    var (f1, f2, texture) = Shader(nif, shaderRef);
                    shapes.Add(new(layer, name ?? "", triangles, vertices, f1, f2, texture));
                }
            }
        }
        return costs.Values.OrderBy(x => x.Layer, StringComparer.Ordinal).ToArray();
    }
}
