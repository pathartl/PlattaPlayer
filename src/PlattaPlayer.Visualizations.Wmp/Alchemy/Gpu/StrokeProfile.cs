using System;
using System.Collections.Generic;

namespace PlattaPlayer.Visualizations.Wmp.Alchemy.Gpu;

/// <summary>
/// The cross-section of a stroke: how strongly a long straight run of <see cref="DrawPrimitives.Plot"/>
/// calls paints the row before the line, the line itself and the row after it, as the weight of the
/// stroke colour against what was already there.
///
/// The GPU draws a stroke as one continuous band instead of a chain of plots. It cannot replay the
/// plot's read-modify-write footprint, and mode 2's in-place 3x3 blur least of all. These weights come
/// from running exactly that footprint, in doubles, along a long line over a plain background. Every
/// plot mode is a chain of lerps toward the colour, so the result is <c>k·colour + (1−k)·background</c>
/// and <c>k</c> is all the band needs. Mode 0 comes out as (0, 0.1, 0), mode 1 as (0.7, 1, 0.7), and mode
/// 2 depends on the neighbour alpha and on the blur's raster order, which is why the two axes are
/// measured separately.
/// </summary>
public static class StrokeProfile
{
    private static readonly Dictionary<(int Mode, float Alpha, bool XMajor), (float Before, float On, float After)> Cache = [];

    /// <summary>
    /// Weights for the row before the line (minor coordinate − 1), on it, and after it (+1).
    /// <paramref name="xMajor"/> is true when the line runs along X.
    /// </summary>
    public static (float Before, float On, float After) For(int mode, float neighbourAlpha, bool xMajor)
    {
        var m = Math.Clamp(mode, 0, 2);
        var key = (m, m == 2 ? neighbourAlpha : 0f, xMajor);
        lock (Cache)
        {
            if (!Cache.TryGetValue(key, out var profile))
            {
                profile = Simulate(m, key.Item2, xMajor);
                Cache[key] = profile;
            }
            return profile;
        }
    }

    private static (float, float, float) Simulate(int mode, float neighbourAlpha, bool xMajor)
    {
        const int size = 64;
        const int minor = size / 2;
        var k = new double[size * size];

        // Plot along the major axis in the walk's positive direction, then read the middle of the run.
        for (var major = 8; major < size - 8; major++)
        {
            var (x, y) = xMajor ? (major, minor) : (minor, major);
            Plot(k, size, x, y, mode, neighbourAlpha);
        }

        double At(int offset)
        {
            var (x, y) = xMajor ? (size / 2, minor + offset) : (minor + offset, size / 2);
            return k[y * size + x];
        }

        return ((float)At(-1), (float)At(0), (float)At(1));
    }

    /// <summary><see cref="DrawPrimitives.Plot"/> on colour weights: colour = 1, background = 0.</summary>
    private static void Plot(double[] k, int stride, int x, int y, int mode, float neighbourAlpha)
    {
        var p = y * stride + x;
        // LerpArgb(colour, existing, t) keeps t of the existing pixel.
        static double Lerp(double existing, double t) => (1 - t) + existing * t;

        switch (mode)
        {
            case 0:
                k[p] = Lerp(k[p], 0.9);
                break;
            case 1:
                k[p] = 1;
                k[p + 1] = Lerp(k[p + 1], 0.3);
                k[p - 1] = Lerp(k[p - 1], 0.3);
                k[p + stride] = Lerp(k[p + stride], 0.3);
                k[p - stride] = Lerp(k[p - stride], 0.3);
                break;
            default:
                k[p] = 1;
                k[p + 1] = Lerp(k[p + 1], neighbourAlpha);
                k[p - 1] = Lerp(k[p - 1], neighbourAlpha);
                k[p + stride] = Lerp(k[p + stride], neighbourAlpha);
                k[p - stride] = Lerp(k[p - stride], neighbourAlpha);
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    var i = p + dy * stride + dx;
                    k[i] = (k[i] + k[i - 1] + k[i + 1] + k[i - stride] + k[i + stride]) / 5;
                }
                break;
        }
    }
}
