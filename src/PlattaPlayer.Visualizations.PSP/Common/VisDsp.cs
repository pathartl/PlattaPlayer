using static PlattaPlayer.Visualizations.PSP.Common.VisMath;

namespace PlattaPlayer.Visualizations.PSP.Common;

/// <summary>
/// The block of PCM the player hands to <see cref="Visualizer.Render"/> every frame. Built by the
/// controller (0x0e28): samples = the 0x14b10 buffer, bytes = 0x1000, rate = 44100. Samples are signed
/// 16-bit, stereo interleaved (L, R), so frames = bytes / 4.
/// </summary>
public sealed class PcmBlock
{
    /// <summary>Bytes in a block as the controller builds it (1024 stereo frames).</summary>
    public const int BlockBytes = 0x1000;

    public PcmBlock(short[] samples, uint bytes, int rate, byte fresh)
    {
        Samples = samples;
        Bytes = bytes;
        Rate = rate;
        Fresh = fresh;
    }

    /// <summary>+0x0: interleaved L, R samples.</summary>
    public short[] Samples { get; }

    /// <summary>+0x4: byte count of <see cref="Samples"/> that is valid.</summary>
    public uint Bytes { get; set; }

    /// <summary>+0x8</summary>
    public int Rate { get; set; }

    /// <summary>+0xc: cleared by the controller after each Render().</summary>
    public byte Fresh { get; set; }
}

public static class VisDsp
{
    /// <summary>
    /// 0xee68: in-place unnormalized DCT-II of n (power of two) floats (Lee's algorithm):
    /// X[k] = sum_i x[i] * cos(pi/n * (i + 0.5) * k).
    /// </summary>
    public static void FastDct(Span<float> a, int n)
    {
        var half = n >> 1;

        // bit-reversal permutation (j is a reversed counter)
        var j = 0;
        for (var i = 1; i < n - 1; i++)
        {
            j ^= half;
            for (var m = half; j < m;)
            {
                m >>= 1;
                j ^= m;
            }
            if (i < j) (a[i], a[j]) = (a[j], a[i]);
        }

        var step = (Atan(1.0f) + Atan(1.0f)) / (float)n; // (pi/2) / n
        if (n >= 2)
        {
            int len = 1, len2 = 2;
            do
            {
                var rev = 0;
                for (var start = 0; start < n; start += len2)
                {
                    var c = Cos(step * (float)(rev + len));
                    c = c + c;
                    rev ^= half;
                    for (var m = half; rev < m;)
                    {
                        m >>= 1;
                        rev ^= m;
                    }
                    var lo = start;
                    var hi = n - len - start;
                    for (var k = 0; k < len; k++)
                    {
                        float x = a[lo + k], y = a[hi + k];
                        a[lo + k] = x + y;
                        a[hi + k] = c * (x - y);
                    }
                }
                len = len2;
                len2 <<= 1;
            } while (len2 <= n);
        }

        for (int q = n >> 2, h = half; q > 0; h = q, q >>= 1)
        {
            for (var k = 0; k < q; k++) a[h + q + k] = -a[h + q + k] - a[q + k];
            for (var i = 2 * h + q; i < n; i += 2 * h)
            {
                for (var k = 0; k < q; k++)
                {
                    a[i + k] = a[i + k] - a[i - h + k];
                    a[i + h + k] = -a[i + h + k] - a[i + k];
                }
            }
        }

        for (var i = 1; i < n; i++) a[i] *= 0.5f;
    }
}

/// <summary>
/// Smoothed RMS meter in dB (class with vtable 0x147d8; ctor 0xf154). Every Update() folds one PCM block
/// in with time constant <c>timeConst</c> seconds and converts to dB; values at or below
/// <c>floorDb</c> read as floorDb.
/// </summary>
public sealed class LevelMeter
{
    private readonly float _timeConst; // +0x04
    private readonly float _floorDb;   // +0x08
    private readonly bool _mono;       // +0x0c
    private float _powL;               // +0x10  smoothed linear RMS (0..1)
    private float _powR;               // +0x14
    private float _dbL;                // +0x18
    private float _dbR;                // +0x1c
    private readonly float _lnDecay;   // +0x20  Log(0.01)
    private readonly float _threshold; // +0x24  Pow10(floorDb / 20)

    /// <summary>0xf154</summary>
    public LevelMeter(float floorDb, float timeConst, bool mono)
    {
        _timeConst = timeConst;
        _floorDb = floorDb;
        _mono = mono;
        _dbL = floorDb;
        _dbR = floorDb;
        _lnDecay = Log(0.01f);
        _threshold = Pow10(_floorDb / 20.0f);
    }

    /// <summary>0xf1cc -> UpdateMono 0xf204 / UpdateStereo 0xf32c</summary>
    public void Update(PcmBlock pcm)
    {
        if (_mono) UpdateMono(pcm);
        else UpdateStereo(pcm);
    }

    /// <summary>0xf4c0: mono: dbL; stereo: (dbL + dbR) / 2</summary>
    public float Db() => _mono ? _dbL : (_dbL + _dbR) * 0.5f;

    public float DbLeft => _dbL;

    public float DbRight => _dbR;

    // 0xf204
    private void UpdateMono(PcmBlock pcm)
    {
        var sum = 0.0f;
        var s = pcm.Samples;
        var p = 0;
        for (var n = pcm.Bytes >> 2; n != 0; n--, p += 2)
        {
            var m = ((int)s[p] + (int)s[p + 1]) / 2;
            sum += (float)(m * m);
        }
        var frames = (float)(pcm.Bytes >> 2);
        var rms = Sqrt((sum + sum) / frames) * 3.05175781e-05f; // / 32768
        var c = Exp(((frames / (float)pcm.Rate) / _timeConst) * _lnDecay);
        _powL = _powL * (1.0f - (1.0f - c)) + rms * (1.0f - c);
        _dbL = (_powL <= _threshold) ? _floorDb : Log10(_powL) * 20.0f;
    }

    // 0xf32c
    private void UpdateStereo(PcmBlock pcm)
    {
        float sumL = 0.0f, sumR = 0.0f;
        var s = pcm.Samples;
        var p = 0;
        for (var n = pcm.Bytes >> 2; n != 0; n--, p += 2)
        {
            sumL += (float)((int)s[p] * (int)s[p]);
            sumR += (float)((int)s[p + 1] * (int)s[p + 1]);
        }
        const float scale = 3.05175781e-05f;
        var frames = (float)(pcm.Bytes >> 2);
        var rmsL = Sqrt((sumL + sumL) / frames) * scale;
        var rmsR = Sqrt((sumR + sumR) / frames);
        var k = 1.0f - Exp(((frames / (float)pcm.Rate) / _timeConst) * _lnDecay);
        _powL = _powL * (1.0f - k) + rmsL * k;
        _powR = _powR * (1.0f - k) + rmsR * scale * k;
        _dbL = (_powL <= _threshold) ? _floorDb : Log10(_powL) * 20.0f;
        _dbR = (_powR <= _threshold) ? _floorDb : Log10(_powR) * 20.0f;
    }
}
