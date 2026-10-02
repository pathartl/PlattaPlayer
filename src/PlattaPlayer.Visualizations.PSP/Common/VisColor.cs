namespace PlattaPlayer.Visualizations.PSP.Common;

/// <summary>Integer HSV &lt;-&gt; RGB helpers (hue in degrees 0..359, s/v 0..255), src/common/vis_color.cpp.</summary>
public static class VisColor
{
    /// <summary>0xeb24: returns 0x00BBGGRR.</summary>
    public static uint HsvToRgb(int hue, byte sat, byte val)
    {
        uint s = sat, v = val;
        if (s == 0) return v | v << 8 | v << 16;
        var h = (hue + 360) % 360;
        var f = (uint)(h % 60);
        var p = (uint)((int)(v * (255 - s)) / 255) & 0xff;
        var q = (uint)((int)(v * (255 - (s * f) / 60)) / 255) & 0xff;
        var t = (uint)((int)(v * (uint)(255 - (int)(s * (60 - f)) / 60)) / 255) & 0xff;
        uint r, g, b;
        switch ((h / 60) % 6)
        {
            case 0: r = v; g = t; b = p; break;
            case 1: r = q; g = v; b = p; break;
            case 2: r = p; g = v; b = t; break;
            case 3: r = p; g = q; b = v; break;
            case 4: r = t; g = p; b = v; break;
            case 5: r = v; g = p; b = q; break;
            default: return 0;
        }
        return r | g << 8 | b << 16;
    }

    /// <summary>0xed1c</summary>
    public static void RgbToHsv(byte r8, byte g8, byte b8, out int hue, out byte sat, out byte val)
    {
        uint r = r8, g = g8, b = b8;
        uint sector = 0, max = b, min, mid = g;
        if (g < r)
        {
            if (r < b)
            {
                sector = 4; min = g; mid = r;
            }
            else
            {
                min = b; max = r;
                if (g < b) { sector = 5; min = g; mid = b; }
            }
        }
        else
        {
            min = r;
            if (g < b)
            {
                sector = 3;
            }
            else
            {
                sector = 2; max = g; mid = b;
                if (b <= r) { sector = 1; min = b; mid = r; }
            }
        }
        val = (byte)max;
        if (max != 0)
        {
            var d = (int)(max - min);
            var s = (uint)(d * 255) / max;
            sat = (byte)s;
            if ((s & 0xff) != 0)
            {
                var x = (int)((max - mid) * 60) / d;
                if ((sector & 1) == 0) x = 60 - x;
                hue = (int)(sector * 60 + x) % 360;
                return;
            }
        }
        else
        {
            sat = 0;
        }
        hue = 0;
    }
}
