using System.Collections.Generic;

namespace PlattaPlayer.Visualizations.MilkDrop.Plugin;

/// <summary>
/// The uniform inputs a translated MilkDrop pixel shader needs each frame: timing, the audio bands, the
/// preset's q-registers, and the render-target geometry. Mirrors MilkDrop's standard shader environment.
/// </summary>
internal readonly struct MilkShaderInputs
{
    public int MainTexture { get; init; }
    public float Time { get; init; }
    public float Fps { get; init; }
    public float Frame { get; init; }
    public float Bass { get; init; }
    public float BassAtt { get; init; }
    public float Mid { get; init; }
    public float MidAtt { get; init; }
    public float Treb { get; init; }
    public float TrebAtt { get; init; }
    public float Vol { get; init; }
    public float Decay { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public float AspectX { get; init; }
    public float AspectY { get; init; }
    public IReadOnlyList<double> Q { get; init; }
}

/// <summary>
/// A compiled, translated MilkDrop pixel shader (warp or comp) plus the cached GL locations of its
/// attributes and the standard MilkDrop uniforms, so the per-frame draw path avoids re-querying them.
/// All of MilkDrop's samplers are pointed at a single bound texture (the source frame): the main sampler
/// is faithful, while blur/noise samplers degrade gracefully to it since the host does not yet generate
/// those auxiliary textures.
/// </summary>
internal sealed class MilkShaderProgram
{
    private static readonly string[] SamplerNames =
    {
        "sampler_main", "sampler_fw_main", "sampler_fc_main", "sampler_pw_main", "sampler_pc_main",
        "sampler_blur1", "sampler_blur2", "sampler_blur3",
        "sampler_noise_lq", "sampler_noise_lq_lite", "sampler_noise_mq", "sampler_noise_hq",
        "sampler_pw_noise_lq",
    };

    private readonly GlBindings _gl;
    private readonly int[] _samplerLocs;
    private readonly int[] _qLocs = new int[32];
    private readonly int _uTime, _uFps, _uFrame, _uProgress;
    private readonly int _uBass, _uBassAtt, _uMid, _uMidAtt, _uTreb, _uTrebAtt, _uVol, _uDecay;
    private readonly int _uTexsize, _uAspect;

    public int Program { get; }
    public int APos { get; }
    public int AUv { get; }

    public MilkShaderProgram(GlBindings gl, string vertexSource, string fragmentSource)
    {
        _gl = gl;
        Program = gl.BuildProgram(vertexSource, fragmentSource);
        APos = gl.GetAttrib(Program, "aPos");
        AUv = gl.GetAttrib(Program, "aUv");

        _samplerLocs = new int[SamplerNames.Length];
        for (var i = 0; i < SamplerNames.Length; i++) _samplerLocs[i] = gl.GetUniform(Program, SamplerNames[i]);
        for (var i = 0; i < 32; i++) _qLocs[i] = gl.GetUniform(Program, "q" + (i + 1));

        _uTime = gl.GetUniform(Program, "time");
        _uFps = gl.GetUniform(Program, "fps");
        _uFrame = gl.GetUniform(Program, "frame");
        _uProgress = gl.GetUniform(Program, "progress");
        _uBass = gl.GetUniform(Program, "bass");
        _uBassAtt = gl.GetUniform(Program, "bass_att");
        _uMid = gl.GetUniform(Program, "mid");
        _uMidAtt = gl.GetUniform(Program, "mid_att");
        _uTreb = gl.GetUniform(Program, "treb");
        _uTrebAtt = gl.GetUniform(Program, "treb_att");
        _uVol = gl.GetUniform(Program, "vol");
        _uDecay = gl.GetUniform(Program, "decay");
        _uTexsize = gl.GetUniform(Program, "texsize");
        _uAspect = gl.GetUniform(Program, "aspect");
    }

    /// <summary>Selects the program and uploads the frame's uniforms; the caller then binds geometry and draws.</summary>
    public void Apply(in MilkShaderInputs s)
    {
        _gl.UseProgram(Program);

        // All samplers read texture unit 0, where the source frame is bound.
        _gl.ActiveTexture(GlBindings.Texture0);
        _gl.BindTexture(GlBindings.Texture2D, s.MainTexture);
        foreach (var loc in _samplerLocs)
            if (loc >= 0) _gl.Uniform1i(loc, 0);

        Set1f(_uTime, s.Time);
        Set1f(_uFps, s.Fps);
        Set1f(_uFrame, s.Frame);
        Set1f(_uProgress, 0f);
        Set1f(_uBass, s.Bass);
        Set1f(_uBassAtt, s.BassAtt);
        Set1f(_uMid, s.Mid);
        Set1f(_uMidAtt, s.MidAtt);
        Set1f(_uTreb, s.Treb);
        Set1f(_uTrebAtt, s.TrebAtt);
        Set1f(_uVol, s.Vol);
        Set1f(_uDecay, s.Decay);

        if (_uTexsize >= 0)
        {
            float w = s.Width <= 0 ? 1 : s.Width;
            float h = s.Height <= 0 ? 1 : s.Height;
            _gl.Uniform4f(_uTexsize, w, h, 1f / w, 1f / h);
        }
        if (_uAspect >= 0)
        {
            var ax = s.AspectX == 0 ? 1f : s.AspectX;
            var ay = s.AspectY == 0 ? 1f : s.AspectY;
            _gl.Uniform4f(_uAspect, ax, ay, 1f / ax, 1f / ay);
        }

        var q = s.Q;
        if (q is not null)
            for (var i = 0; i < 32 && i < q.Count; i++)
                if (_qLocs[i] >= 0) _gl.Uniform1f(_qLocs[i], (float)q[i]);
    }

    private void Set1f(int loc, float value)
    {
        if (loc >= 0) _gl.Uniform1f(loc, value);
    }
}
