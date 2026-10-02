namespace PlattaPlayer.Visualizations.Wmp.BarsAndWaves;

/// <summary>
/// The four presets Windows Media Player's "Bars and Waves" exposes, in the order
/// <c>IWMPEffects::GetPresetTitle</c> reports them.
/// </summary>
public enum BarsAndWavesPreset
{
    Bars = 0,
    OceanMist = 1,
    FireStorm = 2,
    Scope = 3,
}

/// <summary>
/// Mutable render configuration, mirroring the fields wmp.dll's effect object carries.
///
/// It is deliberately MUTABLE state rather than an immutable per-preset record, because
/// <c>SetCurrentPreset</c> in the original does not write every field for every preset — it writes only
/// some, and whatever the previous preset left behind persists. Two of those omissions are visible:
/// selecting Scope never sets the secondary colour, and only Bars ever sets the peak-hold count. A table
/// of complete presets would silently "fix" both and diverge from the real effect.
/// </summary>
internal sealed class BarsAndWavesConfig
{
    // Defaults are the constructor's (wmp.dll FUN_18041ca28), not any preset's.
    public int Background = 0x000000;
    public int Primary = 0xFFFF00;
    public int Secondary = 0x0000FF;

    /// <summary>0 and 4 exist but no preset selects them; 1/2 are bars, 3 is the scope polyline.</summary>
    public int Style = 1;

    /// <summary>0 means "derive from the width"; Bars pins it to 5.</summary>
    public int BarWidth;

    /// <summary>Pixels between bars. 1 for Bars, 0 for the other three.</summary>
    public int Gap = 1;

    public float FallSpeed = 5.0f;
    public float FallAccel = 0.0f;
    public float PeakInitialVelocity = 1.0f;
    public float PeakAccel = 0.2f;
    public int PeakHoldFrames;
    public bool ShowPeaks = true;
    public int TrailMode;
    public int FadeStep = 20;
    public float HeightScale = 1.0f;

    /// <summary>
    /// Applies a preset exactly as <c>FUN_18041e270</c> does — including which fields it leaves alone.
    /// </summary>
    public void Apply(BarsAndWavesPreset preset)
    {
        switch (preset)
        {
            case BarsAndWavesPreset.Bars:
                Background = 0x000000;
                Primary = 0x20B000;   // RGB(0, 176, 32) green
                Secondary = 0xFF2020; // RGB(32, 32, 255) blue
                TrailMode = 0;
                BarWidth = 5;
                Gap = 1;
                FallSpeed = 4.0f;
                PeakHoldFrames = 4;
                Style = 1;
                break;

            case BarsAndWavesPreset.OceanMist:
                Background = 0x000000;
                Primary = 0xFF0000;   // RGB(0, 0, 255) blue
                Secondary = 0xFFFFFF; // white
                BarWidth = 0;
                Gap = 0;
                FallSpeed = 4.0f;
                TrailMode = 4;        // freeze: history keeps its height and only fades
                FadeStep = 20;
                Style = 2;
                break;

            case BarsAndWavesPreset.FireStorm:
                Background = 0x000000;
                Primary = 0x00A5FF;   // RGB(255, 165, 0) orange
                Secondary = 0x0000FF; // RGB(255, 0, 0) red
                BarWidth = 0;
                Gap = 0;
                FallSpeed = 4.0f;
                TrailMode = 1;        // drift: history slides down a pixel per frame
                FadeStep = 15;
                Style = 2;
                break;

            case BarsAndWavesPreset.Scope:
                Background = 0x000000;
                Primary = 0xA0FFA0;   // RGB(160, 255, 160) pale green
                // Secondary is NOT written — Scope inherits the previous preset's. Faithful to the
                // original, and harmless in practice because the scope draws no peaks.
                TrailMode = 0;
                Gap = 0;
                Style = 3;
                break;
        }
    }
}
