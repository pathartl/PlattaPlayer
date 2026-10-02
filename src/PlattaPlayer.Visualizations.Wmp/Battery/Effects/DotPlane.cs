namespace PlattaPlayer.Visualizations.Wmp.Battery.Effects;

/// <summary>
/// <c>CDotPlane</c>: a 3-D height field of N×N particles. Each frame a new row takes its heights from the
/// spectrum. Older rows scroll back in z and fall under gravity. The whole plane rotates on up to three
/// axes and is perspective-projected as 0xFE dots.
///
/// All of it is SINGLE precision. Every matrix product sums strictly left to right,
/// <c>((a0b0 + a1b1) + a2b2) + a3b3</c>, with no FMA. This was recovered element by element from the x64
/// code (<c>Code\battery\04_renders.md</c> §7). Ctor <c>0x180418be8</c>, Randomize <c>0x1804192b0</c>,
/// Render <c>0x1804193a0</c>, UpdateParticles <c>0x180419720</c>, CCamera::Setup <c>0x18041c4e0</c>.
/// </summary>
public sealed class DotPlane : RenderEffect
{
    private const int RowStride = 50;

    // Particle[2500] = { x, y, z, w, v }. v is never initialised by the constructor; it is only read for
    // particles that have been generated, which set it.
    private readonly float[] _px = new float[2500];
    private readonly float[] _py = new float[2500];
    private readonly float[] _pz = new float[2500];
    private readonly float[] _pw = new float[2500];
    private readonly float[] _pv = new float[2500];

    private int _cachedW, _cachedH;
    private float _eyeX = 64.0f, _eyeY = 57.0f, _eyeZ = 51.52600098f;
    private int _ring;
    private readonly int[] _cnt = new int[3];
    private readonly float[] _ang = new float[3];
    private float[] _rx = Identity(), _ry = Identity(), _rz = Identity(), _t = Identity();
    private bool _camDirty;
    private float _dist = 1.0f;
    private bool _matDirty;
    private float[] _combined = new float[16];
    private float _gravity = 0.01f;
    private float _v0;

    // CCamera: up (0,1,0), n = -1, fov = pi/3 (float). Only dir and VP survive Setup.
    private float _dirX, _dirY, _dirZ;
    private float[] _vp = new float[16];

    public DotPlane()
    {
        Flags = 2;
        ResetDisplay();
    }

    public override string Name => "CDotPlane";

    /// <summary>The combined world-view-projection matrix (x64 +0xc67c), exposed so the oracle can check it
    /// bit for bit: a one-ulp error in a product rarely moves a truncated pixel.</summary>
    public ReadOnlySpan<float> CombinedMatrix => _combined;

    private static float[] Identity() => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    /// <summary>Row-major 4×4 float product with the original's summation order.</summary>
    private static float[] Mul(float[] a, float[] b)
    {
        var c = new float[16];
        for (var i = 0; i < 4; i++)
        for (var j = 0; j < 4; j++)
            c[i * 4 + j] = ((a[i * 4] * b[j] + a[i * 4 + 1] * b[4 + j]) + a[i * 4 + 2] * b[8 + j]) + a[i * 4 + 3] * b[12 + j];
        return c;
    }

    /// <summary><c>ResetDisplay</c> (<c>0x180419628</c>). It leaves the particles, the ring, the combined
    /// matrix, camDirty and dist alone.</summary>
    private void ResetDisplay()
    {
        _rx = Identity();
        _ry = Identity();
        _rz = Identity();
        _t = Identity();
        Array.Clear(_ang);
        Array.Clear(_cnt);
        _gravity = 0.01f;
        _v0 = 0;
        _matDirty = true;
    }

    public override void Randomize(CrtRand rand)
    {
        ResetDisplay();
        _cachedW = _cachedH = 0; // forces the camera to be set up again next frame
        P[0] = rand.Next() % 28 + 20;
        int r1 = rand.Next() % 4, r2 = rand.Next() % 4, r3 = rand.Next() % 4;
        // x64: first draw -> bit 0 (Win7 assigns them the other way round).
        P[1] = (r1 == 0 ? 1 : 0) | (r2 == 0 ? 2 : 0) | (r3 == 0 ? 4 : 0);
    }

    public override void OnKey(char key)
    {
        float d;
        if (key == 'D') d = (100f - _dist) - 1f;
        else if (key == 'd') d = (100f - _dist) + 1f;
        else return;
        _dist = -(d - 100f);
        _camDirty = _matDirty = true;
        P[3] = _dist; // mirrored for saving; never read
    }

    public override void Render(BatteryLevels tl, BatteryRenderData rd, CrtRand rand)
    {
        if (rd.W != _cachedW || rd.H != _cachedH)
        {
            _cachedW = rd.W;
            _cachedH = rd.H;
            _eyeX = rd.W / 2;
            _eyeY = rd.H / 2;
            _eyeZ = rd.H / 2;
            _camDirty = _matDirty = true;
            _dist = (float)rd.W * 0.6f;
            P[3] = _dist;
            SetupCamera(rd.W, rd.H);
        }

        UpdateParticles(tl);

        var surf = rd.Canvas;
        var m = _combined;
        for (var r = 0; r < BatteryMath.Trunc(P[0]); r++)
        for (var p = 0; p < BatteryMath.Trunc(P[0]); p++)
        {
            var i = r * RowStride + p;
            float x = _px[i], y = _py[i], z = _pz[i], w = _pw[i];
            var ox = ((x * m[0] + y * m[4]) + z * m[8]) + w * m[12];
            var oy = ((x * m[1] + y * m[5]) + z * m[9]) + w * m[13];
            var ow = ((x * m[3] + y * m[7]) + z * m[11]) + w * m[15];
            if (!(0.0f > ow)) continue;
            var sx = BatteryMath.TruncF(ox / ow);
            if (_cachedW > _cachedH) sx += (_cachedW - _cachedH) >> 1;
            var sy = BatteryMath.TruncF(oy / ow);
            if (sx >= 0 && sx < _cachedW && sy >= 0 && sy < _cachedH) surf.Pixel(sx, sy, 0xFE);
        }
    }

    private void UpdateParticles(BatteryLevels tl)
    {
        if (tl.State != 1) // paused freezes the particles; the rotation still runs
        {
            var k = 1;
            for (var i = 0; i < BatteryMath.Trunc(P[0]); i++)
            {
                var n = BatteryMath.Trunc(P[0]);
                var knext = BatteryMath.TruncF((float)k * 1.096f);
                var idx = _ring * RowStride + i;
                _px[idx] = i - n / 2;
                var f0 = tl[BatteryLevels.Freq0 + k];
                var f1 = tl[BatteryLevels.Freq1 + k];
                var mx = f0 > f1 ? f0 : f1;
                float y = mx >> 2;
                _py[idx] = (y * 0.015f) * y;
                _pz[idx] = -((float)n * 0.5f);
                _pw[idx] = 1.0f;
                _pv[idx] = _v0;
                k = knext > k ? knext : k + 1;
            }

            var nn = BatteryMath.Trunc(P[0]);
            if (++_ring >= nn) _ring -= nn;
            var row = _ring;
            for (var j = 0; j < nn - 1; j++, row++)
            {
                if (row >= nn) row -= nn;
                for (var p = 0; p < nn; p++)
                {
                    var idx = row * RowStride + p;
                    _pz[idx] += 1.0f;
                    if (_py[idx] > 0.0f)
                    {
                        var y = _py[idx] - _pv[idx];
                        _py[idx] = y < 0.0f ? 0.0f : y;
                        _pv[idx] = _pv[idx] + _gravity;
                    }
                }
            }
        }

        var mask = BatteryMath.Trunc(P[1]);
        for (var a = 0; a < 3; a++)
        {
            if ((mask & (1 << a)) == 0) continue;
            _cnt[a]++;
            _ang[a] = ((float)_cnt[a] * 6.2831855f) / 300.0f;
            var c = (float)BatteryMath.Cos(_ang[a]);
            var s = (float)BatteryMath.Sin(_ang[a]);
            var ns = (float)-BatteryMath.Sin(_ang[a]);
            switch (a)
            {
                case 0: _rx = [1, 0, 0, 0, 0, c, ns, 0, 0, s, c, 0, 0, 0, 0, 1]; break;
                case 1: _ry = [c, 0, ns, 0, 0, 1, 0, 0, s, 0, c, 0, 0, 0, 0, 1]; break;
                default: _rz = [c, ns, 0, 0, s, c, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]; break;
            }
            if (_cnt[a] == 300)
            {
                _cnt[a] = 0;
                _ang[a] = 0;
            }
            _matDirty = true;
        }

        if (_camDirty)
        {
            _t = Identity();
            _t[12] = _dist * _dirX;
            _t[13] = _dist * _dirY;
            _t[14] = _dist * _dirZ;
            _camDirty = false;
            _matDirty = true;
        }

        if (_matDirty)
        {
            _combined = Mul(Mul(Mul(Mul(_rx, _ry), _rz), _t), _vp);
            _matDirty = false;
        }
    }

    private static void Normalize(ref float x, ref float y, ref float z)
    {
        var len = (float)BatteryMath.Sqrt((x * x + y * y) + z * z);
        if (!(len > 0)) return;
        x /= len;
        y /= len;
        z /= len;
    }

    /// <summary><c>CCamera::Setup</c> with target (0,0,0), up (0,1,0), n = -1 and fov = pi/3 as float.</summary>
    private void SetupCamera(int w, int h)
    {
        float ex = _eyeX, ey = _eyeY, ez = _eyeZ;
        float fx = ex - 0f, fy = ey - 0f, fz = ez - 0f;
        Normalize(ref fx, ref fy, ref fz);
        _dirX = fx;
        _dirY = fy;
        _dirZ = fz;

        const float upX = 0f, upY = 1.0f, upZ = 0f;
        var d = (upX * fx + upY * fy) + upZ * fz;
        float ux = upX - fx * d, uy = upY - fy * d, uz = upZ - fz * d;
        Normalize(ref ux, ref uy, ref uz);
        float rx = uz * fy - uy * fz, ry = ux * fz - uz * fx, rz = uy * fx - ux * fy;
        Normalize(ref rx, ref ry, ref rz);

        float[] view =
        [
            rx, ux, fx, 0,
            ry, uy, fy, 0,
            rz, uz, fz, 0,
            -((ex * rx + ey * ry) + ez * rz), -((ex * ux + ey * uy) + ez * uz), -((ex * fx + ey * fy) + ez * fz), 1,
        ];

        float m = Math.Min(w, h);
        const float n = -1.0f;
        var n2 = n + 0.01f;
        var t = (float)Math.Tan(1.0471975803375244f * 0.5f);
        var hh = t * n;
        var nh = -hh;
        var p00 = nh == hh ? 1f : (0f - m) / (nh - hh);
        var p11 = hh == nh ? 1f : (0f - m) / (hh - nh);
        var p22 = (n * n) * (1000f - n2) == 0 ? 1f : ((n - n2) * (n - 1000f)) / ((n * n) * (1000f - n2));
        var p3x = nh == hh ? 1f : (m * nh - hh * 0.0f) / (nh - hh);
        var p32 = (n2 - 1000f) * n == 0 ? 1f : ((n - 1000f) * n2) / ((n2 - 1000f) * n);
        float[] proj = [p00, 0, 0, 0, 0, p11, 0, 0, 0, 0, p22, 0, p3x, p3x, p32, 1];
        float[] l = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, n == 0 ? -1f : -1.0f / n, 0, 0, 0, 1];
        _vp = Mul(view, Mul(l, proj));
    }
}
