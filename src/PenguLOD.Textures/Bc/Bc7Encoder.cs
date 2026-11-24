using PenguLOD.Core.Pipeline;

namespace PenguLOD.Textures.Bc;

/// <summary>
/// Multi-mode BC7 encoder evaluating Mode 6 (1-subset RGBA 7.7.7.7+p), Mode 1 (2-subset opaque RGB 6.6.6+shared-p),
/// Mode 5 (1-subset 7-bit RGB + 8-bit rotated scalar channel, rotations 0..3), and Mode 7 (2-subset RGBA 5.5.5.5+p).
/// </summary>
public static class Bc7Encoder
{
    public static byte[] EncodeImage(ReadOnlySpan<byte> rgba, int width, int height) =>
        EncodeImage(rgba.ToArray(), width, height);

    public static byte[] EncodeImage(byte[] rgba, int width, int height)
    {
        if (width <= 0 || height <= 0 || rgba.Length != checked(width * height * 4))
            throw new ArgumentException("Invalid RGBA image dimensions.");
        return TextureCompute.TryEncode(rgba, width, height, out var encoded)
            ? encoded
            : EncodeImageCpu(rgba, width, height);
    }

    public static byte[] EncodeImageCpu(byte[] rgba, int width, int height)
    {
        int bw = (width + 3) / 4, bh = (height + 3) / 4;
        var output = new byte[bw * bh * 16];
        Parallel.For(0, bh, RunLimits.Parallel(), by =>
        {
            Span<byte> block = stackalloc byte[64];
            for (int bx = 0; bx < bw; bx++)
            {
                for (int y = 0; y < 4; y++)
                {
                    int py = Math.Min(by * 4 + y, height - 1);
                    for (int x = 0; x < 4; x++)
                    {
                        int px = Math.Min(bx * 4 + x, width - 1);
                        rgba.AsSpan((py * width + px) * 4, 4).CopyTo(block.Slice((y * 4 + x) * 4, 4));
                    }
                }
                EncodeBlock(block, output.AsSpan((by * bw + bx) * 16, 16));
            }
        });
        return output;
    }

    public static void EncodeBlock(ReadOnlySpan<byte> rgba, Span<byte> dst)
    {
        Span<float> px = stackalloc float[64];
        bool opaque = true;
        bool alphaVaries = false;
        byte aFirst = rgba[3];
        for (int i = 0; i < 16; i++)
        {
            for (int c = 0; c < 4; c++) px[i * 4 + c] = rgba[i * 4 + c];
            byte a = rgba[i * 4 + 3];
            if (a < 255) opaque = false;
            if (a != aFirst) alphaVaries = true;
        }

        // Give fully transparent texels the block's visible average colour so single-subset fits stay on one colour line.
        int visible = 0;
        float vr = 0, vg = 0, vb = 0;
        for (int i = 0; i < 16; i++)
            if (rgba[i * 4 + 3] > 0) { visible++; vr += px[i * 4]; vg += px[i * 4 + 1]; vb += px[i * 4 + 2]; }
        if (visible is > 0 and < 16)
            for (int i = 0; i < 16; i++)
                if (rgba[i * 4 + 3] == 0)
                {
                    px[i * 4] = MathF.Round(vr / visible);
                    px[i * 4 + 1] = MathF.Round(vg / visible);
                    px[i * 4 + 2] = MathF.Round(vb / visible);
                }

        EncodeMode6(px, dst);
        float bestErr = BlockError(rgba, dst);
        if (bestErr <= 16f) return; // near-lossless already (<1 LSB/pixel MSE)

        Span<byte> alt = stackalloc byte[16];
        if (opaque)
        {
            // Mode 1 (2-subset opaque) excels at sharp two-colour edges where single-line Mode 6 blurs across intermediate hues.
            if (bestErr > 64f)
            {
                EncodeMode1(px, alt);
                float err1 = BlockError(rgba, alt);
                if (err1 < bestErr) { alt.CopyTo(dst); bestErr = err1; }
            }
            // Mode 5 with channel rotation (1..3) excels when one RGB channel varies independently (e.g. normal maps).
            if (bestErr > 96f)
            {
                for (int rot = 1; rot <= 3; rot++)
                {
                    EncodeMode5(px, alt, rot);
                    float err5 = BlockError(rgba, alt);
                    if (err5 < bestErr) { alt.CopyTo(dst); bestErr = err5; }
                }
            }
            return;
        }

        if (alphaVaries)
        {
            // Mode 5 rotations 0..3: independent 8-bit scalar channel + 3-channel 7-bit RGB.
            int maxRot = visible == 16 ? 3 : 0; // when fully-transparent texels exist, keep alpha in the 8-bit slot (rot 0)
            for (int rot = 0; rot <= maxRot; rot++)
            {
                EncodeMode5(px, alt, rot);
                float err5 = BlockError(rgba, alt);
                if (err5 < bestErr) { alt.CopyTo(dst); bestErr = err5; }
            }
            // Mode 7 (2-subset RGBA) handles two distinct colours + alpha boundary cleanly.
            if (bestErr > 64f)
            {
                EncodeMode7(px, alt);
                float err7 = BlockError(rgba, alt);
                if (err7 < bestErr) alt.CopyTo(dst);
            }
        }
        else if (bestErr > 64f)
        {
            // Constant non-255 alpha (e.g. normal map with uniform specular gloss < 255): Mode 5 rot 0 stores exact 8-bit alpha,
            // and Mode 7 handles 2-subset RGB edges while preserving non-255 alpha.
            EncodeMode5(px, alt, 0);
            float err5 = BlockError(rgba, alt);
            if (err5 < bestErr) { alt.CopyTo(dst); bestErr = err5; }
            if (bestErr > 64f)
            {
                EncodeMode7(px, alt);
                if (BlockError(rgba, alt) < bestErr) alt.CopyTo(dst);
            }
        }
    }

    private static float BlockError(ReadOnlySpan<byte> original, ReadOnlySpan<byte> block)
    {
        Span<byte> dec = stackalloc byte[64];
        Bc7Decoder.DecodeBlock(block, dec);
        float err = 0;
        for (int i = 0; i < 16; i++)
        {
            float da = original[i * 4 + 3] - dec[i * 4 + 3];
            err += da * da;
            float w = original[i * 4 + 3] / 255f;
            for (int c = 0; c < 3; c++)
            {
                float d = original[i * 4 + c] - dec[i * 4 + c];
                err += w * d * d;
            }
        }
        return err;
    }

    /// <summary>Ranks 64 2-subset partitions by between-subset variance reduction and writes top <paramref name="topK"/> indices.</summary>
    private static void RankPartitions2(ReadOnlySpan<float> px, int channels, Span<int> bestParts)
    {
        Span<float> bestScores = stackalloc float[bestParts.Length];
        bestScores.Fill(-1f);
        Span<float> total = stackalloc float[4];
        for (int i = 0; i < 16; i++)
            for (int c = 0; c < channels; c++) total[c] += px[i * 4 + c];

        var table = Bc7Tables.Partition2;
        Span<float> sum1 = stackalloc float[4];
        for (int p = 0; p < 64; p++)
        {
            int n1 = 0;
            sum1.Clear();
            int row = p * 16;
            for (int i = 0; i < 16; i++)
            {
                if (table[row + i] == 0) continue;
                n1++;
                for (int c = 0; c < channels; c++) sum1[c] += px[i * 4 + c];
            }
            int n0 = 16 - n1;
            if (n0 == 0 || n1 == 0) continue;
            float inv0 = 1f / n0, inv1 = 1f / n1;
            float sep = 0;
            for (int c = 0; c < channels; c++)
            {
                float d = (total[c] - sum1[c]) * inv0 - sum1[c] * inv1;
                sep += d * d;
            }
            float score = sep * (n0 * n1);
            for (int k = 0; k < bestParts.Length; k++)
            {
                if (score > bestScores[k])
                {
                    for (int j = bestParts.Length - 1; j > k; j--)
                    {
                        bestScores[j] = bestScores[j - 1];
                        bestParts[j] = bestParts[j - 1];
                    }
                    bestScores[k] = score;
                    bestParts[k] = p;
                    break;
                }
            }
        }
    }

    private static void SubsetPrincipalEndpoints(ReadOnlySpan<float> px, int part, int subset, int channels, Span<float> e0, Span<float> e1)
    {
        var table = Bc7Tables.Partition2;
        int row = part * 16, count = 0;
        Span<float> mean = stackalloc float[4];
        for (int i = 0; i < 16; i++)
        {
            if (table[row + i] != subset) continue;
            count++;
            for (int c = 0; c < channels; c++) mean[c] += px[i * 4 + c];
        }
        if (count == 0) { e0.Clear(); e1.Clear(); return; }
        for (int c = 0; c < channels; c++) mean[c] /= count;

        Span<float> cov = stackalloc float[16];
        for (int i = 0; i < 16; i++)
        {
            if (table[row + i] != subset) continue;
            for (int a = 0; a < channels; a++)
            {
                float da = px[i * 4 + a] - mean[a];
                for (int b = 0; b < channels; b++) cov[a * 4 + b] += da * (px[i * 4 + b] - mean[b]);
            }
        }
        Span<float> axis = stackalloc float[4];
        int widest = 0;
        for (int c = 1; c < channels; c++) if (cov[c * 4 + c] > cov[widest * 4 + widest]) widest = c;
        for (int c = 0; c < channels; c++) axis[c] = cov[widest * 4 + c];
        if (!Normalize(axis[..channels]))
        {
            for (int c = 0; c < channels; c++) e0[c] = e1[c] = Math.Clamp(mean[c], 0, 255);
            return;
        }
        Span<float> tmp = stackalloc float[4];
        for (int it = 0; it < 6; it++)
        {
            float len = 0;
            for (int a = 0; a < channels; a++)
            {
                float s = 0;
                for (int b = 0; b < channels; b++) s += cov[a * 4 + b] * axis[b];
                tmp[a] = s; len += s * s;
            }
            if (len < 1e-12f) break;
            len = MathF.Sqrt(len);
            for (int a = 0; a < channels; a++) axis[a] = tmp[a] / len;
        }
        float tmin = float.MaxValue, tmax = float.MinValue;
        for (int i = 0; i < 16; i++)
        {
            if (table[row + i] != subset) continue;
            float t = 0;
            for (int c = 0; c < channels; c++) t += (px[i * 4 + c] - mean[c]) * axis[c];
            tmin = MathF.Min(tmin, t); tmax = MathF.Max(tmax, t);
        }
        for (int c = 0; c < channels; c++)
        {
            e0[c] = Math.Clamp(mean[c] + tmin * axis[c], 0, 255);
            e1[c] = Math.Clamp(mean[c] + tmax * axis[c], 0, 255);
        }
    }

    /// <summary>Mode 1: 2 subsets, 6-bit partition, 6-bit RGB + 1 shared p-bit per subset, 3-bit indices.</summary>
    private static void EncodeMode1(ReadOnlySpan<float> px, Span<byte> dst)
    {
        Span<int> topParts = stackalloc int[4];
        RankPartitions2(px, 3, topParts);

        int bestPart = topParts[0];
        float bestTotalErr = float.MaxValue;
        Span<int> bestQ = stackalloc int[12]; // [subset * 6 + ep * 3 + ch]
        Span<int> bestSp = stackalloc int[2];
        Span<int> bestIdx = stackalloc int[16];

        var table = Bc7Tables.Partition2;
        Span<float> e0 = stackalloc float[4];
        Span<float> e1 = stackalloc float[4];
        Span<int> curQ = stackalloc int[12];
        Span<int> curSp = stackalloc int[2];
        Span<int> curIdx = stackalloc int[16];
        Span<int> q0 = stackalloc int[3];
        Span<int> q1 = stackalloc int[3];

        for (int t = 0; t < topParts.Length; t++)
        {
            int part = topParts[t];
            float partErr = 0;
            for (int s = 0; s < 2; s++)
            {
                SubsetPrincipalEndpoints(px, part, s, 3, e0, e1);
                float subBest = float.MaxValue;
                for (int sp = 0; sp < 2; sp++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        q0[c] = QuantizeShared6(e0[c], sp);
                        q1[c] = QuantizeShared6(e1[c], sp);
                    }
                    float err = AssignSubset3(px, part, s, q0, q1, sp, curIdx);
                    if (err < subBest)
                    {
                        subBest = err;
                        curSp[s] = sp;
                        for (int c = 0; c < 3; c++) { curQ[s * 6 + c] = q0[c]; curQ[s * 6 + 3 + c] = q1[c]; }
                    }
                }
                // Re-assign winning sp indices into curIdx for subset s
                AssignSubset3(px, part, s, curQ.Slice(s * 6, 3), curQ.Slice(s * 6 + 3, 3), curSp[s], curIdx);
                partErr += subBest;
            }
            if (partErr < bestTotalErr)
            {
                bestTotalErr = partErr;
                bestPart = part;
                curQ.CopyTo(bestQ);
                curSp.CopyTo(bestSp);
                curIdx.CopyTo(bestIdx);
            }
        }

        int anchor1 = Bc7Tables.Anchor2[bestPart];
        if (bestIdx[0] >= 4)
        {
            for (int c = 0; c < 3; c++) (bestQ[c], bestQ[3 + c]) = (bestQ[3 + c], bestQ[c]);
            for (int i = 0; i < 16; i++) if (table[bestPart * 16 + i] == 0) bestIdx[i] = 7 - bestIdx[i];
        }
        if (bestIdx[anchor1] >= 4)
        {
            for (int c = 0; c < 3; c++) (bestQ[6 + c], bestQ[9 + c]) = (bestQ[9 + c], bestQ[6 + c]);
            for (int i = 0; i < 16; i++) if (table[bestPart * 16 + i] == 1) bestIdx[i] = 7 - bestIdx[i];
        }

        var w = new BitWriter(dst);
        w.Write(0b10, 2); // mode 1
        w.Write(bestPart, 6);
        for (int ch = 0; ch < 3; ch++)
            for (int s = 0; s < 2; s++)
            {
                w.Write(bestQ[s * 6 + ch], 6);
                w.Write(bestQ[s * 6 + 3 + ch], 6);
            }
        w.Write(bestSp[0], 1);
        w.Write(bestSp[1], 1);
        for (int i = 0; i < 16; i++)
            w.Write(bestIdx[i], (i == 0 || i == anchor1) ? 2 : 3);
    }

    private static int QuantizeShared6(float v, int sp)
    {
        // Expanded value is ((q << 1 | sp) << 1) | ((q << 1 | sp) >> 6). Approximate inverse:
        int bestQ = 0;
        float bestD = float.MaxValue;
        int approx = Math.Clamp((int)MathF.Round((v * 127f / 255f - sp) * 0.5f), 0, 63);
        for (int q = Math.Max(0, approx - 1); q <= Math.Min(63, approx + 1); q++)
        {
            int v7 = (q << 1) | sp;
            int exp = (v7 << 1) | (v7 >> 6);
            float d = MathF.Abs(exp - v);
            if (d < bestD) { bestD = d; bestQ = q; }
        }
        return bestQ;
    }

    private static float AssignSubset3(ReadOnlySpan<float> px, int part, int subset, ReadOnlySpan<int> q0, ReadOnlySpan<int> q1, int sp, Span<int> idx)
    {
        Span<int> pal = stackalloc int[24];
        for (int k = 0; k < 8; k++)
        {
            int wt = Bc7Tables.Weights3[k];
            for (int c = 0; c < 3; c++)
            {
                int a7 = (q0[c] << 1) | sp, b7 = (q1[c] << 1) | sp;
                int a = (a7 << 1) | (a7 >> 6), b = (b7 << 1) | (b7 >> 6);
                pal[k * 3 + c] = ((64 - wt) * a + wt * b + 32) >> 6;
            }
        }
        var table = Bc7Tables.Partition2;
        int row = part * 16;
        float total = 0;
        for (int i = 0; i < 16; i++)
        {
            if (table[row + i] != subset) continue;
            float best = float.MaxValue;
            int bi = 0;
            for (int k = 0; k < 8; k++)
            {
                float d = 0;
                for (int c = 0; c < 3; c++) { float t = px[i * 4 + c] - pal[k * 3 + c]; d += t * t; }
                if (d < best) { best = d; bi = k; }
            }
            idx[i] = bi;
            total += best;
        }
        return total;
    }

    /// <summary>Mode 7: 2 subsets, 6-bit partition, 5-bit RGBA + 1 p-bit per endpoint, 2-bit indices.</summary>
    private static void EncodeMode7(ReadOnlySpan<float> px, Span<byte> dst)
    {
        Span<int> topParts = stackalloc int[4];
        RankPartitions2(px, 4, topParts);

        int bestPart = topParts[0];
        float bestTotalErr = float.MaxValue;
        Span<int> bestQ = stackalloc int[16]; // [subset * 8 + ep * 4 + ch]
        Span<int> bestP = stackalloc int[4];  // [subset * 2 + ep]
        Span<int> bestIdx = stackalloc int[16];

        var table = Bc7Tables.Partition2;
        Span<float> e0 = stackalloc float[4];
        Span<float> e1 = stackalloc float[4];
        Span<int> curQ = stackalloc int[16];
        Span<int> curP = stackalloc int[4];
        Span<int> curIdx = stackalloc int[16];

        for (int t = 0; t < topParts.Length; t++)
        {
            int part = topParts[t];
            float partErr = 0;
            for (int s = 0; s < 2; s++)
            {
                SubsetPrincipalEndpoints(px, part, s, 4, e0, e1);
                Quantize5P(e0, curQ.Slice(s * 8, 4), out curP[s * 2]);
                Quantize5P(e1, curQ.Slice(s * 8 + 4, 4), out curP[s * 2 + 1]);
                partErr += AssignSubset4Mode7(px, part, s, curQ.Slice(s * 8, 4), curP[s * 2], curQ.Slice(s * 8 + 4, 4), curP[s * 2 + 1], curIdx);
            }
            if (partErr < bestTotalErr)
            {
                bestTotalErr = partErr;
                bestPart = part;
                curQ.CopyTo(bestQ);
                curP.CopyTo(bestP);
                curIdx.CopyTo(bestIdx);
            }
        }

        int anchor1 = Bc7Tables.Anchor2[bestPart];
        if (bestIdx[0] >= 2)
        {
            for (int c = 0; c < 4; c++) (bestQ[c], bestQ[4 + c]) = (bestQ[4 + c], bestQ[c]);
            (bestP[0], bestP[1]) = (bestP[1], bestP[0]);
            for (int i = 0; i < 16; i++) if (table[bestPart * 16 + i] == 0) bestIdx[i] = 3 - bestIdx[i];
        }
        if (bestIdx[anchor1] >= 2)
        {
            for (int c = 0; c < 4; c++) (bestQ[8 + c], bestQ[12 + c]) = (bestQ[12 + c], bestQ[8 + c]);
            (bestP[2], bestP[3]) = (bestP[3], bestP[2]);
            for (int i = 0; i < 16; i++) if (table[bestPart * 16 + i] == 1) bestIdx[i] = 3 - bestIdx[i];
        }

        var w = new BitWriter(dst);
        w.Write(1 << 7, 8); // mode 7
        w.Write(bestPart, 6);
        for (int ch = 0; ch < 3; ch++)
            for (int s = 0; s < 2; s++)
            {
                w.Write(bestQ[s * 8 + ch], 5);
                w.Write(bestQ[s * 8 + 4 + ch], 5);
            }
        for (int s = 0; s < 2; s++)
        {
            w.Write(bestQ[s * 8 + 3], 5);
            w.Write(bestQ[s * 8 + 4 + 3], 5);
        }
        for (int i = 0; i < 4; i++) w.Write(bestP[i], 1);
        for (int i = 0; i < 16; i++)
            w.Write(bestIdx[i], (i == 0 || i == anchor1) ? 1 : 2);
    }

    private static void Quantize5P(ReadOnlySpan<float> e, Span<int> q, out int pbit)
    {
        float bestErr = float.MaxValue;
        pbit = 0;
        Span<int> cand = stackalloc int[4];
        for (int p = 0; p < 2; p++)
        {
            float err = 0;
            for (int c = 0; c < 4; c++)
            {
                int approx = Math.Clamp((int)MathF.Round((e[c] * 63f / 255f - p) * 0.5f), 0, 31);
                int bestV = approx;
                float bestD = float.MaxValue;
                for (int v = Math.Max(0, approx - 1); v <= Math.Min(31, approx + 1); v++)
                {
                    int v6 = (v << 1) | p;
                    int exp = (v6 << 2) | (v6 >> 4);
                    float d = (exp - e[c]) * (exp - e[c]);
                    if (d < bestD) { bestD = d; bestV = v; }
                }
                cand[c] = bestV;
                err += bestD;
            }
            if (err < bestErr) { bestErr = err; pbit = p; cand.CopyTo(q); }
        }
    }

    private static float AssignSubset4Mode7(ReadOnlySpan<float> px, int part, int subset, ReadOnlySpan<int> q0, int p0, ReadOnlySpan<int> q1, int p1, Span<int> idx)
    {
        Span<int> pal = stackalloc int[16];
        for (int k = 0; k < 4; k++)
        {
            int wt = Bc7Tables.Weights2[k];
            for (int c = 0; c < 4; c++)
            {
                int a6 = (q0[c] << 1) | p0, b6 = (q1[c] << 1) | p1;
                int a = (a6 << 2) | (a6 >> 4), b = (b6 << 2) | (b6 >> 4);
                pal[k * 4 + c] = ((64 - wt) * a + wt * b + 32) >> 6;
            }
        }
        var table = Bc7Tables.Partition2;
        int row = part * 16;
        float total = 0;
        for (int i = 0; i < 16; i++)
        {
            if (table[row + i] != subset) continue;
            float best = float.MaxValue;
            int bi = 0;
            for (int k = 0; k < 4; k++)
            {
                float d = 0;
                for (int c = 0; c < 4; c++) { float t = px[i * 4 + c] - pal[k * 4 + c]; d += t * t; }
                if (d < best) { best = d; bi = k; }
            }
            idx[i] = bi;
            total += best;
        }
        return total;
    }

    private static void EncodeMode6(ReadOnlySpan<float> px, Span<byte> dst)
    {
        bool solid = true;
        for (int i = 1; i < 16 && solid; i++)
            for (int c = 0; c < 4; c++)
                if (px[i * 4 + c] != px[c]) { solid = false; break; }

        Span<float> e0 = stackalloc float[4];
        Span<float> e1 = stackalloc float[4];
        if (solid)
            for (int c = 0; c < 4; c++) e0[c] = e1[c] = px[c];
        else
            PrincipalEndpoints(px, e0, e1);

        Span<int> q0 = stackalloc int[4];
        Span<int> q1 = stackalloc int[4];
        Span<int> idx = stackalloc int[16];
        int p0, p1;

        bool opaque = true;
        for (int i = 0; i < 16 && opaque; i++) opaque = px[i * 4 + 3] >= 255;
        Quantize(e0, q0, out p0, opaque);
        Quantize(e1, q1, out p1, opaque);
        float err = AssignIndices(px, q0, p0, q1, p1, idx);

        if (!solid)
        {
            Span<float> r0 = stackalloc float[4];
            Span<float> r1 = stackalloc float[4];
            if (Refit(px, idx, r0, r1))
            {
                Span<int> rq0 = stackalloc int[4];
                Span<int> rq1 = stackalloc int[4];
                Span<int> ridx = stackalloc int[16];
                Quantize(r0, rq0, out int rp0, opaque);
                Quantize(r1, rq1, out int rp1, opaque);
                float rerr = AssignIndices(px, rq0, rp0, rq1, rp1, ridx);
                if (rerr < err)
                {
                    rq0.CopyTo(q0); rq1.CopyTo(q1); ridx.CopyTo(idx);
                    p0 = rp0; p1 = rp1;
                }
            }
        }

        if (idx[0] >= 8)
        {
            for (int c = 0; c < 4; c++) (q0[c], q1[c]) = (q1[c], q0[c]);
            (p0, p1) = (p1, p0);
            for (int i = 0; i < 16; i++) idx[i] = 15 - idx[i];
        }

        var w = new BitWriter(dst);
        w.Write(1 << 6, 7);
        for (int c = 0; c < 4; c++)
        {
            w.Write(q0[c], 7);
            w.Write(q1[c], 7);
        }
        w.Write(p0, 1);
        w.Write(p1, 1);
        w.Write(idx[0], 3);
        for (int i = 1; i < 16; i++) w.Write(idx[i], 4);
    }

    /// <summary>Mode 5: one subset, RGB 7-bit + scalar 8-bit endpoints with channel rotation 0..3, separate 2-bit colour and scalar indices.</summary>
    private static void EncodeMode5(ReadOnlySpan<float> rawPx, Span<byte> dst, int rotation = 0)
    {
        Span<float> rgb = stackalloc float[64];
        Span<float> scalar = stackalloc float[16];
        for (int i = 0; i < 16; i++)
        {
            float r = rawPx[i * 4], g = rawPx[i * 4 + 1], b = rawPx[i * 4 + 2], a = rawPx[i * 4 + 3];
            switch (rotation)
            {
                case 1: (r, a) = (a, r); break;
                case 2: (g, a) = (a, g); break;
                case 3: (b, a) = (a, b); break;
            }
            rgb[i * 4] = r; rgb[i * 4 + 1] = g; rgb[i * 4 + 2] = b; rgb[i * 4 + 3] = 0;
            scalar[i] = a;
        }
        Span<float> e0 = stackalloc float[4];
        Span<float> e1 = stackalloc float[4];
        PrincipalEndpoints(rgb, e0, e1);

        Span<int> q0 = stackalloc int[3];
        Span<int> q1 = stackalloc int[3];
        Span<int> ci = stackalloc int[16];
        for (int c = 0; c < 3; c++) { q0[c] = Q7(e0[c]); q1[c] = Q7(e1[c]); }
        float err = ColourIndices2(rgb, q0, q1, ci);

        float aa = 0, ab = 0, bb = 0;
        Span<float> ax = stackalloc float[3];
        Span<float> bx = stackalloc float[3];
        for (int i = 0; i < 16; i++)
        {
            float wt = Bc7Tables.Weights2[ci[i]] / 64f, a = 1 - wt;
            aa += a * a; ab += a * wt; bb += wt * wt;
            for (int c = 0; c < 3; c++) { ax[c] += a * rgb[i * 4 + c]; bx[c] += wt * rgb[i * 4 + c]; }
        }
        float det = aa * bb - ab * ab;
        if (MathF.Abs(det) > 1e-6f)
        {
            Span<int> r0 = stackalloc int[3];
            Span<int> r1 = stackalloc int[3];
            Span<int> rci = stackalloc int[16];
            for (int c = 0; c < 3; c++)
            {
                r0[c] = Q7((bb * ax[c] - ab * bx[c]) / det);
                r1[c] = Q7((aa * bx[c] - ab * ax[c]) / det);
            }
            float rerr = ColourIndices2(rgb, r0, r1, rci);
            if (rerr < err) { r0.CopyTo(q0); r1.CopyTo(q1); rci.CopyTo(ci); }
        }

        int amin = 255, amax = 0;
        for (int i = 0; i < 16; i++) { int a = (int)MathF.Round(scalar[i]); amin = Math.Min(amin, a); amax = Math.Max(amax, a); }
        int a0 = Math.Clamp(amin, 0, 255), a1 = Math.Clamp(amax, 0, 255);
        Span<int> ai = stackalloc int[16];
        for (int i = 0; i < 16; i++)
        {
            int best = int.MaxValue;
            int target = (int)MathF.Round(scalar[i]);
            for (int k = 0; k < 4; k++)
            {
                int wt = Bc7Tables.Weights2[k];
                int v = ((64 - wt) * a0 + wt * a1 + 32) >> 6;
                int d = Math.Abs(v - target);
                if (d < best) { best = d; ai[i] = k; }
            }
        }

        if (ci[0] >= 2)
        {
            for (int c = 0; c < 3; c++) (q0[c], q1[c]) = (q1[c], q0[c]);
            for (int i = 0; i < 16; i++) ci[i] = 3 - ci[i];
        }
        if (ai[0] >= 2)
        {
            (a0, a1) = (a1, a0);
            for (int i = 0; i < 16; i++) ai[i] = 3 - ai[i];
        }

        var w = new BitWriter(dst);
        w.Write(1 << 5, 6);
        w.Write(rotation, 2);
        for (int c = 0; c < 3; c++) { w.Write(q0[c], 7); w.Write(q1[c], 7); }
        w.Write(a0, 8);
        w.Write(a1, 8);
        w.Write(ci[0], 1);
        for (int i = 1; i < 16; i++) w.Write(ci[i], 2);
        w.Write(ai[0], 1);
        for (int i = 1; i < 16; i++) w.Write(ai[i], 2);
    }

    private static int Q7(float v) => Math.Clamp((int)MathF.Round(Math.Clamp(v, 0, 255) * 127f / 255f), 0, 127);
    private static int E7(int q) => (q << 1) | (q >> 6);

    private static float ColourIndices2(ReadOnlySpan<float> rgb, ReadOnlySpan<int> q0, ReadOnlySpan<int> q1, Span<int> idx)
    {
        Span<int> pal = stackalloc int[12];
        for (int k = 0; k < 4; k++)
        {
            int wt = Bc7Tables.Weights2[k];
            for (int c = 0; c < 3; c++) pal[k * 3 + c] = ((64 - wt) * E7(q0[c]) + wt * E7(q1[c]) + 32) >> 6;
        }
        float total = 0;
        for (int i = 0; i < 16; i++)
        {
            float best = float.MaxValue;
            for (int k = 0; k < 4; k++)
            {
                float d = 0;
                for (int c = 0; c < 3; c++) { float t = rgb[i * 4 + c] - pal[k * 3 + c]; d += t * t; }
                if (d < best) { best = d; idx[i] = k; }
            }
            total += best;
        }
        return total;
    }

    private static void PrincipalEndpoints(ReadOnlySpan<float> px, Span<float> e0, Span<float> e1)
    {
        Span<float> mean = stackalloc float[4];
        for (int i = 0; i < 16; i++)
            for (int c = 0; c < 4; c++) mean[c] += px[i * 4 + c] / 16f;

        Span<float> cov = stackalloc float[16];
        for (int i = 0; i < 16; i++)
            for (int a = 0; a < 4; a++)
            {
                float da = px[i * 4 + a] - mean[a];
                for (int b = 0; b < 4; b++) cov[a * 4 + b] += da * (px[i * 4 + b] - mean[b]);
            }

        Span<float> axis = stackalloc float[4];
        int widest = 0;
        for (int c = 1; c < 4; c++) if (cov[c * 4 + c] > cov[widest * 4 + widest]) widest = c;
        for (int c = 0; c < 4; c++) axis[c] = cov[widest * 4 + c];
        if (!Normalize(axis))
        {
            for (int c = 0; c < 4; c++)
            {
                float lo = float.MaxValue, hi = float.MinValue;
                for (int i = 0; i < 16; i++) { lo = MathF.Min(lo, px[i * 4 + c]); hi = MathF.Max(hi, px[i * 4 + c]); }
                axis[c] = hi - lo;
            }
            if (!Normalize(axis)) axis.Fill(.5f);
        }
        Span<float> tmp = stackalloc float[4];
        for (int it = 0; it < 8; it++)
        {
            float len = 0;
            for (int a = 0; a < 4; a++)
            {
                float s = 0;
                for (int b = 0; b < 4; b++) s += cov[a * 4 + b] * axis[b];
                tmp[a] = s;
                len += s * s;
            }
            if (len < 1e-12f) break;
            len = MathF.Sqrt(len);
            for (int a = 0; a < 4; a++) axis[a] = tmp[a] / len;
        }

        float tmin = float.MaxValue, tmax = float.MinValue;
        for (int i = 0; i < 16; i++)
        {
            float t = 0;
            for (int c = 0; c < 4; c++) t += (px[i * 4 + c] - mean[c]) * axis[c];
            tmin = MathF.Min(tmin, t);
            tmax = MathF.Max(tmax, t);
        }
        for (int c = 0; c < 4; c++)
        {
            e0[c] = Math.Clamp(mean[c] + tmin * axis[c], 0, 255);
            e1[c] = Math.Clamp(mean[c] + tmax * axis[c], 0, 255);
        }
    }

    private static bool Normalize(Span<float> v)
    {
        float len = 0;
        for (int c = 0; c < v.Length; c++) len += v[c] * v[c];
        if (len < 1e-12f || !float.IsFinite(len)) return false;
        len = MathF.Sqrt(len);
        for (int c = 0; c < v.Length; c++) v[c] /= len;
        return true;
    }

    private static void Quantize(ReadOnlySpan<float> e, Span<int> q, out int pbit, bool opaque = false)
    {
        float bestErr = float.MaxValue;
        pbit = 0;
        Span<int> cand = stackalloc int[4];
        for (int p = opaque ? 1 : 0; p < 2; p++)
        {
            float err = 0;
            for (int c = 0; c < 4; c++)
            {
                int v = Math.Clamp((int)MathF.Round((e[c] - p) / 2f), 0, 127);
                cand[c] = v;
                float d = ((v << 1) | p) - e[c];
                err += d * d;
            }
            if (err < bestErr)
            {
                bestErr = err;
                pbit = p;
                cand.CopyTo(q);
            }
        }
    }

    private static float AssignIndices(ReadOnlySpan<float> px, ReadOnlySpan<int> q0, int p0, ReadOnlySpan<int> q1, int p1, Span<int> idx)
    {
        Span<int> pal = stackalloc int[64];
        for (int k = 0; k < 16; k++)
        {
            int w = Bc7Tables.Weights4[k];
            for (int c = 0; c < 4; c++)
            {
                int a = (q0[c] << 1) | p0, b = (q1[c] << 1) | p1;
                pal[k * 4 + c] = ((64 - w) * a + w * b + 32) >> 6;
            }
        }

        float total = 0;
        for (int i = 0; i < 16; i++)
        {
            float best = float.MaxValue;
            int bi = 0;
            for (int k = 0; k < 16; k++)
            {
                float d = 0;
                for (int c = 0; c < 4; c++)
                {
                    float t = px[i * 4 + c] - pal[k * 4 + c];
                    d += t * t;
                }
                if (d < best) { best = d; bi = k; }
            }
            idx[i] = bi;
            total += best;
        }
        return total;
    }

    private static bool Refit(ReadOnlySpan<float> px, ReadOnlySpan<int> idx, Span<float> e0, Span<float> e1)
    {
        float aa = 0, ab = 0, bb = 0;
        Span<float> ax = stackalloc float[4];
        Span<float> bx = stackalloc float[4];
        for (int i = 0; i < 16; i++)
        {
            float w = Bc7Tables.Weights4[idx[i]] / 64f;
            float a = 1 - w, b = w;
            aa += a * a; ab += a * b; bb += b * b;
            for (int c = 0; c < 4; c++)
            {
                ax[c] += a * px[i * 4 + c];
                bx[c] += b * px[i * 4 + c];
            }
        }
        float det = aa * bb - ab * ab;
        if (MathF.Abs(det) < 1e-6f) return false;
        float inv = 1f / det;
        for (int c = 0; c < 4; c++)
        {
            e0[c] = Math.Clamp((bb * ax[c] - ab * bx[c]) * inv, 0, 255);
            e1[c] = Math.Clamp((aa * bx[c] - ab * ax[c]) * inv, 0, 255);
        }
        return true;
    }

    private ref struct BitWriter(Span<byte> dst)
    {
        private readonly Span<byte> _dst = dst;
        private int _pos;

        public void Write(int value, int bits)
        {
            for (int i = 0; i < bits; i++, _pos++)
            {
                if ((_pos & 7) == 0) _dst[_pos >> 3] = 0;
                _dst[_pos >> 3] |= (byte)(((value >> i) & 1) << (_pos & 7));
            }
        }
    }
}
