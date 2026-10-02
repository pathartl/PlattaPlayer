namespace PlattaPlayer.Visualizations.MilkDrop.Plugin;

/// <summary>
/// A small built-in MilkDrop preset so the visualizer shows something the moment audio plays, before
/// any <c>.milk</c> files are wired up. Written in the real preset format and parsed through the same
/// pipeline as external presets, so it also exercises the parser and equation engine.
/// </summary>
internal static class DefaultPresets
{
    public const string Name = "PlattaPlayer Default";

    public const string Source = """
        fDecay=0.97
        zoom=1.0
        rot=0.0
        cx=0.5
        cy=0.5
        warp=1.0
        wave_r=1.0
        wave_g=0.6
        wave_b=0.2
        wave_a=1.0
        per_frame_1=zoom = 1.0 + 0.03*(bass - 1);
        per_frame_2=rot = 0.008*sin(time*0.3) + 0.012*(treb - 1);
        per_frame_3=warp = 1.0 + 0.6*bass_att;
        per_frame_4=dx = 0.003*sin(time*0.7);
        per_frame_5=dy = 0.003*cos(time*0.6);
        per_frame_6=wave_r = 0.5 + 0.5*sin(time*1.3);
        per_frame_7=wave_g = 0.5 + 0.5*sin(time*1.7 + 2.1);
        per_frame_8=wave_b = 0.5 + 0.5*sin(time*2.3 + 4.2);
        per_frame_9=decay = 0.965 + 0.02*bass_att;
        """;
}
