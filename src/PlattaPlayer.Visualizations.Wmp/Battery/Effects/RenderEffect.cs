namespace PlattaPlayer.Visualizations.Wmp.Battery.Effects;

/// <summary>
/// <c>CRenderEffect</c>: something drawn into the field each frame, either before the warp (a "pre
/// shift") or after it (a "post shift"). The flags word decides which pre-effects a shift suppresses:
/// the five border effects carry 1, and CTileShift carries 1. Derivation: <c>Code\battery\04_renders.md</c>.
/// </summary>
public abstract class RenderEffect : MemoryEffect
{
    /// <summary>Slot 4: <c>Render(TimedLevel*, CRenderData*)</c>. <paramref name="rand"/> is the shared
    /// CRT stream; only CJDar draws from it per frame.</summary>
    public abstract void Render(BatteryLevels tl, BatteryRenderData rd, CrtRand rand);

    /// <summary>Slot 5. Only CDotPlane reacts ('D'/'d' move the camera).</summary>
    public virtual void OnKey(char key)
    {
    }

    /// <summary>The render pool in <c>CRenderData</c>'s construction order. CJDar's constructor draws 6
    /// times and CGalaxy's 2, in that order, after the shift pool is built.</summary>
    public static RenderEffect[] CreatePool(CrtRand rand)
    {
        var edgeTrace = new EdgeTrace();
        var edgeGradiant = new EdgeGradiant();
        var cosEdge = new CosEdgeGradiant();
        var waveEdge = new WaveEdge();
        var spectrumEdge = new SpectrumEdge();
        var circle = new CircleWaveform();
        var dotPlane = new DotPlane();
        var jdar = new JDar(rand);
        var galaxy = new Galaxy(rand);
        var jiggy = new JiggyScribble();
        return [edgeTrace, edgeGradiant, cosEdge, waveEdge, spectrumEdge, circle, dotPlane, jdar, galaxy, jiggy];
    }

    /// <summary>The four one-pixel border lines the gradient and spectrum edges draw, in their order.</summary>
    protected static void Border(IBatteryCanvas s, int w, int h, byte c)
    {
        s.Line(0, 0, w - 1, 0, c);
        s.Line(w - 1, 0, w - 1, h - 1, c);
        s.Line(w - 1, h - 1, 0, h - 1, c);
        s.Line(0, h - 1, 0, 0, c);
    }
}

/// <summary><c>JBall</c>: a box bouncing inside a rectangle, used by CJDar and CGalaxy. It integrates
/// in float and truncates to int positions every step.</summary>
public sealed class JBall
{
    public int Ix, Iy;
    public int MinX, MinY;
    public int MaxX = 512, MaxY = 352; // hard-coded, not the field size
    public int SizeX = 40, SizeY = 40;
    public float Vx, Vy;
    public float Fx, Fy;

    public void SetPosition(int x, int y)
    {
        Ix = x;
        Iy = y;
        Fx = x;
        Fy = y;
    }

    /// <summary><c>JBall::move</c> (<c>0x180418b4c</c>).</summary>
    public void Move(float dt)
    {
        Fx = Vx * dt + Fx;
        Fy = Vy * dt + Fy;
        Ix = BatteryMath.TruncF(Fx);
        Iy = BatteryMath.TruncF(Fy);
        if ((Ix + SizeX >= MaxX && Vx > 0) || (Ix <= MinX && Vx < 0)) Vx = -Vx;
        if ((Iy <= MinY && Vy < 0) || (Iy + SizeY >= MaxY && Vy > 0)) Vy = -Vy;
    }
}

/// <summary><c>CEdgeTrace</c>: marching 0xF5/0xFF dashes round the border. Randomize <c>0x180416a00</c>,
/// Render <c>0x1804178d0</c>.</summary>
public sealed class EdgeTrace : RenderEffect
{
    private int _phase;

    public EdgeTrace()
    {
        Flags = 1;
        P[0] = 50.0;
    }

    public override string Name => "CEdgeTrace";

    public override void Randomize(CrtRand rand) => P[0] = rand.Next() % 30 + 35;

    public override void Render(BatteryLevels tl, BatteryRenderData rd, CrtRand rand)
    {
        var s = rd.Canvas;
        int w = rd.W, h = rd.H;
        var step = BatteryMath.Trunc(P[0]);
        var pos = (_phase + 1) % step + 3;
        if (pos <= 3) pos = 3;
        _phase = pos;

        for (; pos < w - 4; pos += BatteryMath.Trunc(P[0]))
        {
            s.Pixel(pos - 3, 0, 0xF5); s.Pixel(pos - 2, 0, 0xFF); s.Pixel(pos - 1, 0, 0xFF); s.Pixel(pos, 0, 0xF5);
            s.Pixel(pos - 2, 1, 0xF5); s.Pixel(pos - 1, 1, 0xF5);
        }
        pos -= w;
        if (pos < 3) pos += step;
        for (; pos < h - 4; pos += BatteryMath.Trunc(P[0]))
        {
            s.Pixel(w - 1, pos - 3, 0xF5); s.Pixel(w - 1, pos - 2, 0xFF);
            s.Pixel(w - 1, pos - 1, 0xFF); s.Pixel(w - 1, pos, 0xF5);
            s.Pixel(w - 2, pos - 2, 0xF5); s.Pixel(w - 2, pos - 1, 0xF5);
        }
        pos -= h;
        if (pos < 3) pos += step;
        for (; pos < w - 4; pos += BatteryMath.Trunc(P[0]))
        {
            var x = w - pos;
            s.Pixel(x - 3, h - 1, 0xF5); s.Pixel(x - 2, h - 1, 0xFF); s.Pixel(x - 1, h - 1, 0xFF); s.Pixel(x, h - 1, 0xF5);
            s.Pixel(x - 2, h - 2, 0xF5); s.Pixel(x - 1, h - 2, 0xF5);
        }
        pos -= w;
        if (pos < 3) pos += step;
        for (; pos < h - 4; pos += BatteryMath.Trunc(P[0]))
        {
            var y = h - pos;
            s.Pixel(0, y - 3, 0xF5); s.Pixel(0, y - 2, 0xFF); s.Pixel(0, y - 1, 0xFF); s.Pixel(0, y, 0xF5);
            s.Pixel(1, y - 2, 0xF5); s.Pixel(1, y - 1, 0xF5);
        }
    }
}

/// <summary><c>CEdgeGradiant</c>: a border whose colour ramps 0..255 and back. It has no Randomize.
/// Render <c>0x1804177e0</c>.</summary>
public sealed class EdgeGradiant : RenderEffect
{
    private int _v;
    private int _dir = 1;

    public EdgeGradiant() => Flags = 1;

    public override string Name => "CEdgeGradiant";

    public override void Randomize(CrtRand rand)
    {
    }

    public override void Render(BatteryLevels tl, BatteryRenderData rd, CrtRand rand)
    {
        if ((uint)_v > 255) _dir = -_dir; // unsigned: v = -1 also turns it
        _v += _dir;
        Border(rd.Canvas, rd.W, rd.H, (byte)_v); // one frame each of 0x00 at 256 and 0xFF at -1
    }
}

/// <summary><c>CCosEdgeGradiant</c>: a border with a cosine colour. Randomize <c>0x1804169b0</c>, Render
/// <c>0x1804176f0</c>.</summary>
public sealed class CosEdgeGradiant : RenderEffect
{
    private double _phase;

    public CosEdgeGradiant() => Flags = 1;

    public override string Name => "CCosEdgeGradiant";

    public override void Randomize(CrtRand rand) => P[0] = (float)(rand.Unit() * 0.09f);

    public override void Render(BatteryLevels tl, BatteryRenderData rd, CrtRand rand)
    {
        _phase = P[0] + _phase;
        if (_phase > BatteryMath.TwoPiSingle) _phase = 0.0;
        Border(rd.Canvas, rd.W, rd.H, (byte)BatteryMath.Trunc(BatteryMath.Cos(_phase) * 253.0 + 1.0));
    }
}

/// <summary><c>CWaveEdge</c>: raw waveform or spectrum bytes written straight onto the border. Randomize
/// <c>0x180417290</c>, Render <c>0x180418a30</c>.</summary>
public sealed class WaveEdge : RenderEffect
{
    public WaveEdge() => Flags = 1;

    public override string Name => "CWaveEdge";

    public override void Randomize(CrtRand rand) => P[0] = (float)(rand.Next() % 2);

    public override void Render(BatteryLevels tl, BatteryRenderData rd, CrtRand rand)
    {
        int a, c;
        if (P[0] == 0.0) { a = BatteryLevels.Wave0; c = BatteryLevels.Wave1; }
        else { a = BatteryLevels.Freq0; c = BatteryLevels.Freq1; }
        var s = rd.Canvas;
        int w = rd.W, h = rd.H;
        for (var x = 0; x < w - 1; x++)
        {
            s.Pixel(x, 0, tl[a + x % 1024]);
            s.Pixel(x, h - 1, tl[c + x % 1024]);
        }
        for (var y = 0; y < h - 1; y++)
        {
            s.Pixel(0, y, tl[a + (y & 1023)]);
            s.Pixel(w - 1, y, tl[c + (y & 1023)]);
        }
    }
}

/// <summary><c>CSpectrumEdge</c>: a border in one colour, the spectrum level smoothed over up to 15
/// frames. Randomize <c>0x1804171c0</c>, Render <c>0x180418860</c>.</summary>
public sealed class SpectrumEdge : RenderEffect
{
    private readonly int[] _hist = new int[15];
    private int _idx;

    public SpectrumEdge() => Flags = 1;

    public override string Name => "CSpectrumEdge";

    public override void Randomize(CrtRand rand)
    {
        P[0] = (float)(rand.Next() % 10);
        P[1] = (float)(rand.Next() % 10 * 2 + 2);
        P[2] = (float)(rand.Next() % 11 + 4);
    }

    public override void Render(BatteryLevels tl, BatteryRenderData rd, CrtRand rand)
    {
        if (P[1] == 0.0) return;
        if (!(P[2] > 0.0)) return;
        var n = BatteryMath.Trunc(P[2]);
        if (n > 15) return;
        if (BatteryMath.Trunc(P[1]) >= 1023) return;

        _idx = (_idx + 1) % n;
        _hist[_idx] = 0;
        for (var i = 0; P[1] > i; i += 2)
            _hist[_idx] += tl[BatteryLevels.Freq0 + i] + tl[0x401 + i];

        var total = 0;
        for (byte k = 0; P[2] > k; k++) total += _hist[k];
        var v = BatteryMath.Trunc(total / (P[2] * P[1]));
        byte c;
        if (P[0] == 0.0) c = (byte)~(byte)v;
        else if (P[0] > 5.0) c = (byte)BatteryMath.TruncF((byte)v * 0.9f);
        else c = (byte)v;
        Border(rd.Canvas, rd.W, rd.H, c);
    }
}

/// <summary><c>CCircleWaveform</c>: one to three pairs of half-circle oscilloscopes, channel 0 below and
/// channel 1 mirrored above. When there is more than one pair, the centres orbit. Randomize
/// <c>0x1804168c0</c>, Render <c>0x1804172d0</c>.</summary>
public sealed class CircleWaveform : RenderEffect
{
    private const int BaseRadius = 20;
    private float _spin;

    public CircleWaveform()
    {
        Flags = 2;
        P[1] = 1.0;
    }

    public override string Name => "CCircleWaveform";

    public override void Randomize(CrtRand rand)
    {
        int r1 = rand.Next() % 5, r2 = rand.Next() % 5, r3 = rand.Next() % 4;
        P[0] = r3;
        P[1] = 1 + (r1 == 0 ? 1 : 0) + (r2 == 0 ? 1 : 0);
        P[2] = (float)(rand.Unit() * 0.6f + 0.05f);
    }

    public override void Render(BatteryLevels tl, BatteryRenderData rd, CrtRand rand)
    {
        var surf = rd.Canvas;
        byte cA = 0, cB = 0;
        var amp = BatteryMath.TruncF(tl[BatteryLevels.Freq0] * 0.00390625f * (float)(rd.H >> 1));
        int pAx = 0, pAy = 0, pBx = 0, pBy = 0;

        for (var k = 0; k < BatteryMath.Trunc(P[1]); k++)
        {
            int cx = rd.W >> 1, cy = rd.H >> 1;
            if (BatteryMath.Trunc(P[1]) > 1)
            {
                var ang = 6.2831855f / (float)P[1] * k + _spin;
                double rad = (float)(rd.W >> 3);
                cx += BatteryMath.Trunc(BatteryMath.Cos(ang) * rad);
                cy += BatteryMath.Trunc(BatteryMath.Sin(ang) * rad);
            }

            int n;
            if (P[2] > 1.0)
            {
                P[2] = 1.0; // permanently clamps the parameter
                n = 1024;
            }
            else
            {
                n = BatteryMath.Trunc(P[2] * 1024.0);
                if (n <= 0) continue;
            }

            int a = BatteryLevels.Wave0, b = BatteryLevels.Wave1;
            for (var i = 0; i < n; i++)
            {
                var t = (float)i / n;
                var af = t * 3.1415927f;
                switch (BatteryMath.Trunc(P[0]))
                {
                    case 0:
                        cA = (byte)(2 * Math.Abs(tl[a] - 128));
                        cB = (byte)(2 * Math.Abs(tl[b] - 128));
                        break;
                    case 1:
                        cA = tl[a];
                        cB = tl[b];
                        break;
                    case 2:
                        cA = cB = 0xFF;
                        break;
                    case 3:
                    {
                        var v = BatteryMath.TruncF(t * 768.0f);
                        var c = (byte)v;
                        if (((v / 256) & 1) != 0) c = (byte)~c;
                        cA = cB = c;
                        break;
                    }
                }

                double ad = af;
                double rA = (float)amp * (tl[a] * 0.00390625f) + (float)BaseRadius;
                a++;
                var xA = BatteryMath.Trunc(BatteryMath.Cos(ad) * rA) + cx;
                var yA = BatteryMath.Trunc(BatteryMath.Sin(ad) * rA) + cy;
                if (i != 0) surf.DrawClippedLine(pAx, pAy, xA, yA, cA);
                pAx = xA;
                pAy = yA;

                double rB = (float)amp * (tl[b] * 0.00390625f) + (float)BaseRadius;
                b++;
                var xB = BatteryMath.Trunc(BatteryMath.Cos(ad) * rB) + cx;
                var yB = cy - BatteryMath.Trunc(BatteryMath.Sin(ad) * rB);
                if (i != 0) surf.DrawClippedLine(pBx, pBy, xB, yB, cB);
                pBx = xB;
                pBy = yB;
            }
        }

        _spin += 0.04908738657832146f; // pi/64, added in float
    }
}

/// <summary><c>CJDar</c>: spiral J-curves from a spectrum-driven polyline toward a bouncing ball. It is
/// the only effect that draws from <c>rand()</c> every frame. Ctor <c>0x180416514</c> (6 draws), Randomize
/// <c>0x180416da0</c>, Render <c>0x180418020</c>.</summary>
public sealed class JDar : RenderEffect
{
    private readonly JBall _ball = new();
    private byte _colour;
    private double _angle;
    private int _segs;
    private int _alt;

    public JDar(CrtRand rand)
    {
        Flags = 2;
        P[1] = 1.0;
        P[6] = 100.0;
        _colour = (byte)(rand.Next() % 256);
        _angle = (float)(rand.Unit() * 3.1415927f);
        var a = rand.Next() % 200;
        var b = rand.Next() % 200;
        _ball.SetPosition(b, a);
        var va = rand.Unit();
        var vb = rand.Unit();
        _ball.Vy = va; // x64: the first draw is vy (Win7 is the other way round)
        _ball.Vx = vb;
    }

    public override string Name => "CJDar";

    public override void Randomize(CrtRand rand)
    {
        P[0] = rand.Next() % 100 + 5;
        P[1] = (float)(rand.Next() % 4);
        P[2] = rand.Next() % 2;
        P[3] = (float)Math.Pow(2.0, rand.Next() % 4 + 2);
        P[4] = (float)(rand.Unit() * 1.5f + 0.3f);
        P[5] = (float)(rand.Next() % 2);
        P[6] = rand.Next() % 95 + 5;
        _colour = (byte)(rand.Next() % 256);
        var a = rand.Next() % 200;
        var b = rand.Next() % 200;
        _ball.SetPosition(b, a);
        _ball.Vy = rand.Unit();
        _ball.Vx = rand.Unit();
    }

    public override void Render(BatteryLevels tl, BatteryRenderData rd, CrtRand rand)
    {
        if (P[3] == 0.0) return;
        _segs = BatteryMath.Trunc(P[3]);
        _ball.Move(6.0f);

        var s3f = (float)(tl[BatteryLevels.Freq0 + 1] + tl[BatteryLevels.Freq1] + tl[BatteryLevels.Freq0]) / 9000.0f;
        double s3 = s3f;
        _colour = (byte)((_colour - BatteryMath.Trunc(s3 * -6.0)) % 256);
        _angle = s3 + _angle;
        if (_angle > BatteryMath.TwoPiSingle) _angle -= BatteryMath.TwoPiSingle;

        int w = rd.W, h = rd.H;
        int cx = w >> 1, cy = h >> 1, prevX = cx, prevY = cy;
        var jitter = rand.Next() % (w >> 1);
        var sum = 0;
        for (var j = 0; j < 512; j++) sum += tl[2 * j] + tl[0x401 + 2 * j];
        var step = 1024 / _segs;
        if (sum == 0 || step == 0 || _segs <= 0) return;

        double d = (float)step * 255.0f;
        var cum = 0;
        var surf = rd.Canvas;
        var off = 0;
        for (var i = 0; i < _segs; i++, off += step)
        {
            var s = 0.0;
            if (step > 1)
            {
                for (var j = 0; j < ((step - 2) >> 1) + 1; j++)
                {
                    s += tl[0x400 - off - 2 * j];
                    s += tl[0x7FF - off - 2 * j];
                }
            }

            cum += BatteryMath.Trunc(s);
            var dth = (s / d) * P[4];
            var rad = ((double)cum * jitter) / sum;
            switch (BatteryMath.Trunc(P[1]))
            {
                case 0:
                    if ((i & 1) == 0) dth = -dth;
                    break;
                case 1:
                    _alt = (_alt + 1) % 2;
                    if (_alt == 0) dth = -dth;
                    break;
                default:
                    if (rand.Next() % 2 == 1) dth = -dth;
                    break;
            }

            var a = _angle + dth;
            var x = BatteryMath.Trunc(BatteryMath.Cos(a) * rad) + cx;
            var y = BatteryMath.Trunc(BatteryMath.Sin(a) * rad) + cy;
            var conn = P[5] == 0.0;
            surf.DrawJCurve(prevX, prevY, x, y, _ball.Ix, _ball.Iy, 100, _colour, 0xFF, conn, 3);
            if (P[2] == 0.0)
                surf.DrawJCurve(w - prevX, h - prevY, w - x, h - y, w - _ball.Ix, h - _ball.Iy, 100, _colour, 0xFF, conn, 3);
            prevX = x;
            prevY = y;
        }
    }
}

/// <summary><c>CGalaxy</c>: pairs of spiral arms about an orbiting ball. Ctor <c>0x180416414</c> (2
/// draws), Randomize <c>0x180416a60</c>, Render <c>0x180417c70</c>, DoGalaxyArmPair
/// <c>0x1804166d8</c>.</summary>
public sealed class Galaxy : RenderEffect
{
    private readonly JBall _ball = new();
    private double _armAngle;

    public Galaxy(CrtRand rand)
    {
        Flags = 2;
        var va = rand.Unit() * 5.6f;
        var vb = rand.Unit() * 5.6f;
        _ball.Vy = va; // x64: first draw is vy
        _ball.Vx = vb;
    }

    public override string Name => "CGalaxy";

    public override void Randomize(CrtRand rand)
    {
        P[0] = rand.Next() % 5 + 1;
        var q = rand.Next() % 4;
        P[1] = q == 0 ? rand.Next() % 100 + 9 : rand.Next() % 20 + 10;
        P[2] = P[0] == 0.0 ? 0.10000000149011612 : (double)(float)(rand.Unit() * 0.4f + 0.05f) / P[0];
        P[3] = rand.Next() % 56 + 200;
        P[4] = rand.Next() % 10;
        P[5] = rand.Next() % 150;
        P[6] = (float)(rand.Unit() * 6.000000212225132e-07f);
        P[7] = (float)(rand.Unit() * 0.3f);
        if (3.0 > P[5])
        {
            var a = rand.Next() % 200;
            var b = rand.Next() % 200;
            _ball.SetPosition(b, a);
        }
        else
        {
            _ball.SetPosition(0, 0);
        }
        _ball.Vy = rand.Unit() * 5.6f;
        _ball.Vx = rand.Unit() * 5.6f;
    }

    public override void Render(BatteryLevels tl, BatteryRenderData rd, CrtRand rand)
    {
        var surf = rd.Canvas;
        if (2.0 > P[5])
        {
            var r2 = _ball.Fx * _ball.Fx + _ball.Fy * _ball.Fy;
            var ax = ((double)r2 * (double)-_ball.Fx * P[6]) - (P[7] * _ball.Vx);
            var ay = ((double)r2 * (double)-_ball.Fy * P[6]) - (P[7] * _ball.Vy);
            var a6 = tl.Bass6() / 1530.0;
            var sp = BatteryMath.Sqrt(_ball.Vy * _ball.Vy + _ball.Vx * _ball.Vx);
            var px = sp == 0 ? 0.0 : _ball.Vy / sp;
            var py = sp == 0 ? 0.0 : -_ball.Vx / sp;
            _ball.MaxX = surf.W;
            _ball.MaxY = surf.H;
            _ball.MinX = -surf.W;
            _ball.MinY = -surf.H;
            _ball.Vx = (float)(px * a6 + ax) + _ball.Vx;
            _ball.Vy = (float)(py * a6 + ay) + _ball.Vy;
            _ball.Move(0.6f);
        }

        var s6 = tl.Bass6() / 1530.0;
        _armAngle = s6 * P[2] + _armAngle;
        if (_armAngle > BatteryMath.TwoPiSingle) _armAngle -= BatteryMath.TwoPiSingle;
        var arms = BatteryMath.Trunc(P[0]);
        var dA = arms > 0 ? BatteryMath.PiSingle / arms : 0.5;
        var big = (double)(float)surf.H + P[5];
        if (arms > 50) arms = 50;
        for (var i = 0; i < arms; i++)
        {
            var len = (tl.Bass6(i) / 1530.0) * big;
            var len2 = (tl[BatteryLevels.Wave1 + i] / 255.0) * len;
            var w0 = tl[BatteryLevels.Wave0 + i] / 255.0;
            ArmPair(rd, i * dA + _armAngle, len, len2, w0);
        }
    }

    private void ArmPair(BatteryRenderData rd, double ang, double len, double len2, double w0)
    {
        int cx = (rd.W >> 1) + _ball.Ix, cy = (rd.H >> 1) + _ball.Iy;
        var ex = BatteryMath.Trunc(BatteryMath.Cos(ang) * len);
        var ey = BatteryMath.Trunc(BatteryMath.Sin(ang) * len);
        var k = 1.0 - w0;
        var mx = BatteryMath.Trunc(BatteryMath.Cos(ang) * len2) + BatteryMath.Trunc(ey * k);
        var my = BatteryMath.Trunc(BatteryMath.Sin(ang) * len2) - BatteryMath.Trunc(ex * k);
        var steps = BatteryMath.Trunc(P[3]);
        var cEnd = (byte)BatteryMath.Trunc(P[3]);
        var conn = P[4] == 0.0;
        rd.Canvas.DrawJCurve(cx, cy, cx + ex, cy + ey, cx + mx, cy + my, steps, 0xFF, cEnd, conn, 0);
        rd.Canvas.DrawJCurve(cx, cy, cx - ex, cy - ey, cx - mx, cy - my, steps, 0xFF, cEnd, conn, 0);
    }
}

/// <summary><c>CJiggyScribble</c>: a rotating epicycle (rose) curve whose radius follows the bass, drawn
/// as pixels or lines. Randomize <c>0x180417010</c>, Render <c>0x180418470</c>.</summary>
public sealed class JiggyScribble : RenderEffect
{
    private double _rot;

    public JiggyScribble() => Flags = 2;

    public override string Name => "CJiggyScribble";

    public override void Randomize(CrtRand rand)
    {
        P[0] = rand.Next() % 100 + 4;
        P[1] = rand.Next() % 200 + 40;
        P[2] = rand.Next() % 1000 + 300;
        P[3] = rand.Next() % 20 + 1;
        P[4] = (float)(rand.Unit() * 0.6f + 0.05f);
        P[5] = rand.Next() % 10;
        P[6] = rand.Next() % 10;
    }

    public override void Render(BatteryLevels tl, BatteryRenderData rd, CrtRand rand)
    {
        var surf = rd.Canvas;
        var lines = P[6] == 9.0;
        var dt = P[2] == 0.0 ? 0.0 : BatteryMath.TwoPiSingle / P[2];
        var s6 = tl.Bass6();
        _rot = P[4] + _rot;
        var amp = (s6 / 1530.0) * (P[1] - P[0]);
        if (_rot > BatteryMath.TwoPiSingle) _rot -= BatteryMath.TwoPiSingle;
        double cx = surf.W >> 1, cy = surf.H >> 1;
        double prevX = 0, prevY = 0;
        if (lines)
        {
            var a0 = P[3] * 0.0;
            var px0 = BatteryMath.Cos(a0) * P[0] + BatteryMath.Cos(0.0) * amp;
            var py0 = BatteryMath.Sin(a0) * P[0] + BatteryMath.Sin(0.0) * amp;
            var r0 = BatteryMath.Sqrt(px0 * px0 + py0 * py0);
            var th0 = BatteryMath.Atan2(px0, py0) + _rot; // atan2(px, py): the arguments really are swapped
            prevX = BatteryMath.Cos(th0) * r0 + cx;
            prevY = BatteryMath.Sin(th0) * r0 + cy;
        }

        var t = 0.0;
        for (var i = 0; i < BatteryMath.Trunc(P[2]); i++, t += dt)
        {
            var a = t * P[3];
            var px = BatteryMath.Cos(t) * amp + BatteryMath.Cos(a) * P[0];
            var py = BatteryMath.Sin(t) * amp + BatteryMath.Sin(a) * P[0];
            var r = BatteryMath.Sqrt(py * py + px * px);
            var th = BatteryMath.Atan2(px, py) + _rot;
            var x = BatteryMath.Cos(th) * r + cx;
            var y = BatteryMath.Sin(th) * r + cy;
            if (lines)
            {
                surf.DrawClippedLine(BatteryMath.Trunc(prevX), BatteryMath.Trunc(prevY), BatteryMath.Trunc(x), BatteryMath.Trunc(y), 0xFF);
                prevX = x;
                prevY = y;
            }
            else if (x >= 0 && y >= 0 && surf.W > x && surf.H > y)
            {
                surf.Pixel(BatteryMath.Trunc(x), BatteryMath.Trunc(y), 0xFF);
            }
        }
    }
}
