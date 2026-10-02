namespace PlattaPlayer.Visualizations.MilkDrop;

/// <summary>
/// The per-frame motion and colour parameters the renderer consumes, produced by running a preset's
/// per-frame equations. These mirror MilkDrop's classic global variables: the warp/zoom/rotation that
/// feed the frame-to-frame feedback transform, the decay that fades the previous frame, and the wave
/// colour used to draw the waveform.
/// </summary>
public struct FrameState
{
    public double Decay;
    public double Gamma;

    // Feedback transform.
    public double Zoom;
    public double Rot;
    public double Warp;
    public double Cx;
    public double Cy;
    public double Dx;
    public double Dy;
    public double Sx;
    public double Sy;

    // Waveform appearance.
    public double WaveR;
    public double WaveG;
    public double WaveB;
    public double WaveA;
    public double WaveX;
    public double WaveY;
    public double WaveMystery;
    public int WaveMode;

    // Video echo.
    public double EchoZoom;
    public double EchoAlpha;
    public int EchoOrient;
}
