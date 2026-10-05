using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Visualizations.Abstractions;
using PlattaPlayer.Visualizations.Wmp.Alchemy.Gpu;
using PlattaPlayer.Visualizations.Wmp.Audio;

namespace PlattaPlayer.Visualizations.Wmp.Alchemy.Plugin;

/// <summary>
/// Alchemy at the window's own resolution, on the GPU.
///
/// The original renders a fixed 640x480 field and stretches it, blocky and squashed, to the window
/// (<see cref="WmpAlchemyCpuVisualizer"/> reproduces that exactly). This control runs the same effect
/// with every length scaled to the window, so features keep their size relative to the frame but are
/// drawn with the window's pixels. A window that is not 4:3 gets a wider (or taller) field rather than
/// a stretched one, so circles stay round (<see cref="AlchemyFieldScale"/>).
///
/// The split: <see cref="AlchemyGpuEngine"/> runs the scheduling, the kernels' parameters and the
/// renderers' strokes on the CPU, unchanged from the exact port. <see cref="AlchemyGlRenderer"/> does
/// every per-pixel step on the GPU. On any GL failure it logs to <see cref="DataDirectory"/> and goes
/// black, as the MilkDrop plugin does.
///
/// Effects are randomly scheduled, so next / previous / random all ask for a new warp kernel, the
/// nearest equivalent of the original's 'm' key.
/// </summary>
public sealed class WmpAlchemyVisualizer : OpenGlControlBase, IVisualizationController, IVisualizerHealth
{
    public static readonly StyledProperty<IAudioTap?> TapProperty =
        AvaloniaProperty.Register<WmpAlchemyVisualizer, IAudioTap?>(nameof(Tap));

    public IAudioTap? Tap
    {
        get => GetValue(TapProperty);
        set => SetValue(TapProperty, value);
    }

    /// <summary>Private writable folder for the GL logs (host-supplied). When null, logging is skipped.</summary>
    public string? DataDirectory { get; set; }

    /// <summary>Raised (on the render thread) whenever the active effect changes; carries its name.</summary>
    public event Action<string>? NameChanged;

    /// <summary>
    /// The rate the control is paced at, matching WMP's visualization timer. Every motion constant in the
    /// effect is per-frame, so this IS the animation speed.
    /// </summary>
    private const double NominalFps = 1000.0 / WmpFrameRate.WindowedIntervalMs;

    private readonly AlchemyGpuEngine _engine = new(new Random(Environment.TickCount));
    private readonly AlchemyGpuFrame _frame = new();
    private readonly GpuWarpScaler _scaler = new();
    private readonly TapToTimedLevel _adapter = new();
    private readonly TimedLevels _levels = new();
    private DispatcherTimer? _pacer;
    private volatile bool _pendingRandom;
    private string _lastName = "";

    private AlchemyGlRenderer? _renderer;
    private bool _failed;
    private long _heartbeat;
    private bool _loggedRender;

    public void RandomPreset() { _pendingRandom = true; RequestNextFrameRendering(); }

    public void NextPreset() => RandomPreset();

    public void PreviousPreset() => RandomPreset();

    public long Heartbeat => Interlocked.Read(ref _heartbeat);

    public void Resume()
    {
        if (_pacer is { IsEnabled: false }) _pacer.Start();
        RequestNextFrameRendering();
    }

    /// <summary>
    /// Paces redraws at WMP's visualization rate with a timer. Requesting the next frame from inside the
    /// render free-runs at the compositor's rate (239 Hz where it was measured), which animated the effect
    /// several times too fast.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _pacer = new DispatcherTimer { Interval = WmpFrameRate.WindowedInterval };
        _pacer.Tick += (_, _) => RequestNextFrameRendering();
        _pacer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _pacer?.Stop();
        _pacer = null;
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            var isGles = GlVersion.Type == GlProfileType.OpenGLES;
            _renderer = new AlchemyGlRenderer(new GlBindings(gl), isGles);
            _renderer.Init();
            LogInfo($"init ok (gles={isGles})");
        }
        catch (Exception ex)
        {
            _failed = true;
            LogFailure("init", ex);
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _renderer?.Dispose();
        _renderer = null;
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_failed || _renderer is null) return;
        try { RenderFrame(_renderer, fb); }
        catch (Exception ex) { _failed = true; LogFailure("render", ex); return; }
        Interlocked.Increment(ref _heartbeat);
    }

    private void RenderFrame(AlchemyGlRenderer renderer, int fb)
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var deviceW = Math.Max(1, (int)Math.Round(Bounds.Width * scaling));
        var deviceH = Math.Max(1, (int)Math.Round(Bounds.Height * scaling));

        var scale = AlchemyFieldScale.For(deviceW, deviceH);
        if (scale != renderer.Scale)
        {
            renderer.EnsureSize(scale);
            _engine.Resize(scale.FieldWidth, scale.FieldHeight);
            LogInfo($"field {scale.FieldWidth}x{scale.FieldHeight} at scale {scale.Scale:0.###} " +
                    $"-> {scale.DeviceWidth}x{scale.DeviceHeight}{(scale.IsExact ? " (exact)" : "")}");
        }

        _levels.LoadFrom(_adapter.Update(Tap));

        if (_pendingRandom)
        {
            _pendingRandom = false;
            _engine.Reshuffle();
        }

        // The engine gates itself on fresh audio, as the original does: while paused or stopped the field
        // holds still and is only re-presented.
        if (_engine.Update(_levels, NominalFps, _frame))
        {
            _scaler.Update(_frame, scale.Scale);
            renderer.Step(_frame, _scaler);
            if (!_loggedRender)
            {
                _loggedRender = true;
                LogInfo($"first advancing frame: '{_frame.CurrentName}', {_frame.Strokes.Batches.Count} overlay batches");
            }
        }

        renderer.Present(fb, deviceW, deviceH);
        RaiseNameIfChanged();
    }

    private void RaiseNameIfChanged()
    {
        var name = _engine.CurrentName;
        if (name == _lastName) return;
        _lastName = name;
        NameChanged?.Invoke(name);
    }

    private void LogFailure(string phase, Exception ex)
    {
        Debug.WriteLine($"WMP Alchemy GL {phase} failed: {ex}");
        try
        {
            if (string.IsNullOrEmpty(DataDirectory)) return;
            File.AppendAllText(Path.Combine(DataDirectory, "wmp-alchemy-error.log"),
                               $"[{DateTime.Now:O}] WMP Alchemy GL {phase} failed:\n{ex}\n\n");
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// One-shot milestones. A black visualization with no failure log is ambiguous (GL never ran, or ran
    /// and drew nothing), and these tell the two apart without growing the log in steady state.
    /// </summary>
    private void LogInfo(string message)
    {
        Debug.WriteLine($"WMP Alchemy: {message}");
        try
        {
            if (string.IsNullOrEmpty(DataDirectory)) return;
            File.AppendAllText(Path.Combine(DataDirectory, "wmp-alchemy.log"), $"[{DateTime.Now:O}] {message}\n");
        }
        catch { /* ignore */ }
    }
}
