using PenguLOD.Core.Pipeline;

namespace PenguLOD.Textures.Bc;

/// <summary>
/// Managed BC5 (RGTC2 / ATI2, 8 bpp) 2-channel encoder for tangent-space normal maps (R = X, G = Y; Z reconstructed by shader/decoder).
/// Each 4x4 channel block uses 8-bit endpoints with 8-step linear interpolation (<c>e0 &gt; e1</c>).
/// </summary>
public static class Bc5Encoder
{
    public static byte[] EncodeImage(ReadOnlySpan<byte> rgba, int width, int height)
    {
        int bw = (width + 3) / 4, bh = (height + 3) / 4;
        var dst = new byte[bw * bh * 16];
        var src = rgba.ToArray();
        Parallel.For(0, bh, RunLimits.Parallel(), by =>
        {
            Span<byte> chR = stackalloc byte[16];
            Span<byte> chG = stackalloc byte[16];
            for (int bx = 0; bx < bw; bx++)
            {
                for (int dy = 0; dy < 4; dy++)
                {
                    int sy = Math.Min(by * 4 + dy, height - 1);
                    for (int dx = 0; dx < 4; dx++)
                    {
                        int sx = Math.Min(bx * 4 + dx, width - 1);
                        int o = (sy * width + sx) * 4;
                        chR[dy * 4 + dx] = src[o];
                        chG[dy * 4 + dx] = src[o + 1];
                    }
                }
                int dstOff = (by * bw + bx) * 16;
                EncodeChannel(chR, dst.AsSpan(dstOff, 8));
                EncodeChannel(chG, dst.AsSpan(dstOff + 8, 8));
            }
        });
        return dst;
    }

    public static void EncodeChannel(ReadOnlySpan<byte> values, Span<byte> dst)
    {
        byte lo = 255, hi = 0;
        for (int i = 0; i < 16; i++)
        {
            byte v = values[i];
            if (v < lo) lo = v;
            if (v > hi) hi = v;
        }

        if (hi == lo)
        {
            dst[0] = hi;
            dst[1] = lo;
            dst.Slice(2, 6).Clear();
            return;
        }

        // e0 > e1 selects 8 interpolated levels: [0]=e0, [1]=e1, [2..7] = ((7-k)*e0 + (k-1)*e1)/7
        dst[0] = hi;
        dst[1] = lo;
        Span<int> palette = stackalloc int[8];
        palette[0] = hi;
        palette[1] = lo;
        for (int k = 1; k <= 6; k++)
            palette[k + 1] = ((7 - k) * hi + k * lo + 3) / 7;

        ulong bits = 0;
        for (int i = 0; i < 16; i++)
        {
            int v = values[i];
            int bestIdx = 0, bestErr = int.MaxValue;
            for (int k = 0; k < 8; k++)
            {
                int err = Math.Abs(v - palette[k]);
                if (err < bestErr) { bestErr = err; bestIdx = k; }
            }
            bits |= (ulong)(uint)bestIdx << (i * 3);
        }

        for (int b = 0; b < 6; b++)
            dst[2 + b] = (byte)((bits >> (b * 8)) & 0xFF);
    }
}
