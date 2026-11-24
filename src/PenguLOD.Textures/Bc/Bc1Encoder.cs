using System.Numerics;
using PenguLOD.Core.Pipeline;

namespace PenguLOD.Textures.Bc;

/// <summary>
/// Fast managed BC1 (DXT1, 4 bpp) encoder using covariance PCA + least-squares RGB565 endpoint refit.
/// Supports opaque 4-colour blocks (<c>c0 &gt; c1</c>) and optional 1-bit punch-through alpha (<c>c0 &lt;= c1</c>).
/// </summary>
public static class Bc1Encoder
{
    public static byte[] EncodeImage(ReadOnlySpan<byte> rgba, int width, int height, bool punchThroughAlpha = false, byte alphaThreshold = 128)
    {
        int bw = (width + 3) / 4, bh = (height + 3) / 4;
        var dst = new byte[bw * bh * 8];
        var src = rgba.ToArray();
        Parallel.For(0, bh, RunLimits.Parallel(), by =>
        {
            Span<byte> block = stackalloc byte[64];
            for (int bx = 0; bx < bw; bx++)
            {
                ExtractBlock(src, width, height, bx * 4, by * 4, block);
                EncodeBlock(block, dst.AsSpan((by * bw + bx) * 8, 8), punchThroughAlpha, alphaThreshold);
            }
        });
        return dst;
    }

    private static void ExtractBlock(byte[] rgba, int w, int h, int x0, int y0, Span<byte> block)
    {
        for (int dy = 0; dy < 4; dy++)
        {
            int sy = Math.Min(y0 + dy, h - 1);
            for (int dx = 0; dx < 4; dx++)
            {
                int sx = Math.Min(x0 + dx, w - 1);
                rgba.AsSpan((sy * w + sx) * 4, 4).CopyTo(block.Slice((dy * 4 + dx) * 4, 4));
            }
        }
    }

    public static void EncodeBlock(ReadOnlySpan<byte> rgba, Span<byte> dst, bool punchThroughAlpha = false, byte alphaThreshold = 128)
    {
        Span<Vector3> px = stackalloc Vector3[16];
        Span<bool> transparent = stackalloc bool[16];
        int visible = 0;
        Vector3 mean = Vector3.Zero;
        for (int i = 0; i < 16; i++)
        {
            var c = new Vector3(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2]);
            px[i] = c;
            bool trans = punchThroughAlpha && rgba[i * 4 + 3] < alphaThreshold;
            transparent[i] = trans;
            if (!trans) { mean += c; visible++; }
        }

        if (visible == 0)
        {
            // Fully transparent 1-bit alpha block: c0 = 0, c1 = 0, all indices = 3 (transparent).
            dst[0] = dst[1] = dst[2] = dst[3] = 0;
            dst[4] = dst[5] = dst[6] = dst[7] = 0xFF;
            return;
        }

        mean /= visible;
        bool hasTrans = visible < 16;

        // Covariance + power iteration for principal colour axis.
        float cxx = 0, cxy = 0, cxz = 0, cyy = 0, cyz = 0, czz = 0;
        Vector3 minC = new(255f), maxC = Vector3.Zero;
        for (int i = 0; i < 16; i++)
        {
            if (transparent[i]) continue;
            var d = px[i] - mean;
            cxx += d.X * d.X; cxy += d.X * d.Y; cxz += d.X * d.Z;
            cyy += d.Y * d.Y; cyz += d.Y * d.Z; czz += d.Z * d.Z;
            minC = Vector3.Min(minC, px[i]);
            maxC = Vector3.Max(maxC, px[i]);
        }

        var span = maxC - minC;
        if (span.LengthSquared() < 1e-3f)
        {
            ushort packed = Pack565(mean);
            if (!hasTrans)
            {
                // Solid block: c0 = c1 -> index 0 selects c0 cleanly regardless of ordering.
                WriteU16(dst, 0, packed);
                WriteU16(dst, 2, packed);
                dst[4] = dst[5] = dst[6] = dst[7] = 0;
                return;
            }
            WriteU16(dst, 0, packed);
            WriteU16(dst, 2, packed);
            uint idxBits = 0;
            for (int i = 0; i < 16; i++) if (transparent[i]) idxBits |= 3u << (i * 2);
            WriteU32(dst, 4, idxBits);
            return;
        }

        Vector3 axis = span.Y >= span.X && span.Y >= span.Z ? Vector3.UnitY
            : span.X >= span.Z ? Vector3.UnitX : Vector3.UnitZ;
        for (int it = 0; it < 6; it++)
        {
            var next = new Vector3(
                cxx * axis.X + cxy * axis.Y + cxz * axis.Z,
                cxy * axis.X + cyy * axis.Y + cyz * axis.Z,
                cxz * axis.X + cyz * axis.Y + czz * axis.Z);
            float len = next.Length();
            if (len < 1e-6f) break;
            axis = next / len;
        }

        float minProj = float.MaxValue, maxProj = float.MinValue;
        for (int i = 0; i < 16; i++)
        {
            if (transparent[i]) continue;
            float p = Vector3.Dot(px[i] - mean, axis);
            if (p < minProj) minProj = p;
            if (p > maxProj) maxProj = p;
        }

        Vector3 e0 = ClampColor(mean + axis * minProj);
        Vector3 e1 = ClampColor(mean + axis * maxProj);

        if (!hasTrans)
        {
            // 1 step of 4-point least-squares endpoint refinement before RGB565 quantization.
            Refit4(px, ref e0, ref e1);
            ushort q0 = Pack565(e0), q1 = Pack565(e1);
            if (q0 == q1)
            {
                if (q0 < 0xFFFF) q0++;
                else q1--;
            }
            if (q0 < q1) (q0, q1) = (q1, q0); // c0 > c1 signals 4-colour opaque mode

            Vector3 c0 = Unpack565(q0), c1 = Unpack565(q1);
            Vector3 c2 = (2f * c0 + c1) / 3f, c3 = (c0 + 2f * c1) / 3f;
            uint indices = 0;
            for (int i = 0; i < 16; i++)
            {
                float d0 = DistSq(px[i], c0), d1 = DistSq(px[i], c1), d2 = DistSq(px[i], c2), d3 = DistSq(px[i], c3);
                uint best = 0; float bestD = d0;
                if (d1 < bestD) { bestD = d1; best = 1; }
                if (d2 < bestD) { bestD = d2; best = 2; }
                if (d3 < bestD) { best = 3; }
                indices |= best << (i * 2);
            }
            WriteU16(dst, 0, q0);
            WriteU16(dst, 2, q1);
            WriteU32(dst, 4, indices);
        }
        else
        {
            ushort q0 = Pack565(e0), q1 = Pack565(e1);
            if (q0 > q1) (q0, q1) = (q1, q0); // c0 <= c1 signals 3-colour + 1-bit transparent mode
            Vector3 c0 = Unpack565(q0), c1 = Unpack565(q1), c2 = (c0 + c1) * 0.5f;
            uint indices = 0;
            for (int i = 0; i < 16; i++)
            {
                if (transparent[i]) { indices |= 3u << (i * 2); continue; }
                float d0 = DistSq(px[i], c0), d1 = DistSq(px[i], c1), d2 = DistSq(px[i], c2);
                uint best = 0; float bestD = d0;
                if (d1 < bestD) { bestD = d1; best = 1; }
                if (d2 < bestD) { best = 2; }
                indices |= best << (i * 2);
            }
            WriteU16(dst, 0, q0);
            WriteU16(dst, 2, q1);
            WriteU32(dst, 4, indices);
        }
    }

    private static void Refit4(ReadOnlySpan<Vector3> px, ref Vector3 e0, ref Vector3 e1)
    {
        var dir = e1 - e0;
        float invLenSq = dir.LengthSquared();
        if (invLenSq < 1e-6f) return;
        invLenSq = 1f / invLenSq;
        float a00 = 0, a01 = 0, a11 = 0;
        Vector3 b0 = Vector3.Zero, b1 = Vector3.Zero;
        for (int i = 0; i < 16; i++)
        {
            float t = Math.Clamp(Vector3.Dot(px[i] - e0, dir) * invLenSq, 0f, 1f);
            float w = MathF.Round(t * 3f) / 3f;
            float u = 1f - w;
            a00 += u * u; a01 += u * w; a11 += w * w;
            b0 += px[i] * u; b1 += px[i] * w;
        }
        float det = a00 * a11 - a01 * a01;
        if (MathF.Abs(det) < 1e-5f) return;
        float invDet = 1f / det;
        e0 = ClampColor((b0 * a11 - b1 * a01) * invDet);
        e1 = ClampColor((b1 * a00 - b0 * a01) * invDet);
    }

    private static Vector3 ClampColor(Vector3 c) => Vector3.Clamp(c, Vector3.Zero, new Vector3(255f));
    private static float DistSq(Vector3 a, Vector3 b) { var d = a - b; return d.X * d.X + d.Y * d.Y + d.Z * d.Z; }

    private static ushort Pack565(Vector3 c)
    {
        int r = (int)Math.Clamp(MathF.Round(c.X * 31f / 255f), 0f, 31f);
        int g = (int)Math.Clamp(MathF.Round(c.Y * 63f / 255f), 0f, 63f);
        int b = (int)Math.Clamp(MathF.Round(c.Z * 31f / 255f), 0f, 31f);
        return (ushort)((r << 11) | (g << 5) | b);
    }

    private static Vector3 Unpack565(ushort v)
    {
        int r = (v >> 11) & 31, g = (v >> 5) & 63, b = v & 31;
        return new((r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2));
    }

    private static void WriteU16(Span<byte> dst, int offset, ushort value)
    {
        dst[offset] = (byte)value;
        dst[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteU32(Span<byte> dst, int offset, uint value)
    {
        dst[offset] = (byte)value;
        dst[offset + 1] = (byte)(value >> 8);
        dst[offset + 2] = (byte)(value >> 16);
        dst[offset + 3] = (byte)(value >> 24);
    }
}
