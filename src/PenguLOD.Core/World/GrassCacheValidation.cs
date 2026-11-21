using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace PenguLOD.Core.World;

public sealed record GrassCacheInfo(int Types, long Instances);
public sealed record GrassCacheTypeInfo(string Model, uint RuntimeFormId, long Instances);

public static class GrassCacheValidation
{
    /// <summary>
    /// Validate the whole NGIO stream without allocating an object for every blade. Strict, for a cache PenguLOD generated and is
    /// about to publish: 16 half floats a blade, blocks that fit the game's 256 KiB scratch buffer, and nothing after the last type.
    /// </summary>
    public static GrassCacheInfo Inspect(Stream stream, CancellationToken ct = default)
        => Inspect(stream, null, true, strict: true, ct);

    /// <summary>
    /// Read type identities and instance counts without decoding blades. Declared payload sizes are checked before seeking.
    /// Reads any installed cache the way the game and <see cref="GrassCache.Read"/> do: only the types the file declares, so bytes
    /// after them are ignored, a blade of 6 to 64 half floats, and the model path up to its first NUL. Caches made in game or by other
    /// tools differ from FasterNGIO's in these.
    /// </summary>
    public static IReadOnlyList<GrassCacheTypeInfo> ReadTypes(Stream stream, CancellationToken ct = default)
    {
        var result = new List<GrassCacheTypeInfo>();
        Inspect(stream, result, false, strict: false, ct);
        return result;
    }

    private static GrassCacheInfo Inspect(Stream stream, List<GrassCacheTypeInfo>? identities, bool validatePositions, bool strict, CancellationToken ct)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var types = reader.ReadUInt32();
        if (types > 4096) throw new InvalidDataException("Invalid grass type count.");
        long instances = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            for (int type = 0; type < types; type++)
            {
                ct.ThrowIfCancellationRequested();
                uint length = reader.ReadUInt32();
                if (length is < 2 or > 1024) throw new InvalidDataException("Invalid grass model path length.");
                stream.ReadExactly(buffer.AsSpan(0, (int)length));
                string model;
                if (strict)
                {
                    if (buffer[length - 1] != 0) throw new InvalidDataException("Unterminated grass model path.");
                    model = Encoding.UTF8.GetString(buffer, 0, (int)length - 1);
                    GamePath.ValidateRelative(model);
                }
                else
                {
                    int end = Array.IndexOf(buffer, (byte)0, 0, (int)length);
                    model = Encoding.UTF8.GetString(buffer, 0, end < 0 ? (int)length : end);
                }
                if (!float.IsFinite(reader.ReadSingle()) && strict) throw new InvalidDataException("Invalid grass wave period.");
                uint runtimeId = reader.ReadUInt32();
                for (int i = 0; i < 3; i++) if (reader.ReadByte() > 1 && strict) throw new InvalidDataException("Invalid grass flags.");
                uint chunks = reader.ReadUInt32();
                if (chunks > 1048576) throw new InvalidDataException("Invalid grass chunk count.");
                long typeInstances = 0;
                for (int chunk = 0; chunk < chunks; chunk++)
                {
                    ct.ThrowIfCancellationRequested();
                    for (int i = 0; i < 7; i++) if (!float.IsFinite(reader.ReadSingle()) && strict) throw new InvalidDataException("Invalid grass chunk bounds.");
                    uint count = reader.ReadUInt32(), halves = reader.ReadUInt32();
                    // Skyrim's reader has a fixed 256 KiB scratch buffer for each block.
                    if (strict && (halves != 16 || count > 8192)) throw new InvalidDataException("Unsafe grass instance block size.");
                    if (!strict && (halves is < 6 or > 64 || count > 1 << 24)) throw new InvalidDataException($"Invalid grass instance block ({count} blades of {halves} half floats).");
                    long bytes = (long)count * halves * 2;
                    if (!validatePositions && stream.CanSeek)
                    {
                        if (bytes > stream.Length - stream.Position) throw new EndOfStreamException("Truncated grass instance block.");
                        stream.Seek(bytes, SeekOrigin.Current);
                    }
                    else if (!validatePositions)
                    {
                        for (long left = bytes; left > 0; left -= buffer.Length)
                            stream.ReadExactly(buffer.AsSpan(0, (int)Math.Min(left, buffer.Length)));
                    }
                    else
                    {
                        stream.ReadExactly(buffer.AsSpan(0, (int)bytes));
                        if (validatePositions)
                            for (int offset = 0; offset < bytes; offset += 32)
                                for (int component = 0; component < 4; component++)
                                    if (!Half.IsFinite(BinaryPrimitives.ReadHalfLittleEndian(buffer.AsSpan(offset + component * 2))))
                                        throw new InvalidDataException("Invalid grass position or scale.");
                    }
                    instances += count;
                    typeInstances += count;
                }
                identities?.Add(new(model, runtimeId, typeInstances));
            }
            if (strict && stream.ReadByte() != -1) throw new InvalidDataException("Unexpected trailing grass cache data.");
            return new((int)types, instances);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
