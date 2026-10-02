using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Visualizations.Abstractions;
using PlattaPlayer.Visualizations.Wmp.Audio;
using PlattaPlayer.Visualizations.Wmp.Battery.Gpu;

namespace PlattaPlayer.Visualizations.Wmp.Battery.Plugin;

/// <summary>
/// Battery at the window's own resolution, on the GPU.
///
/// The original renders a fixed 384x288 field and stretches it, blocky and squashed, to the window
/// (<see cref="WmpBatteryCpuVisualizer"/> reproduces that exactly). This control runs the same effect
/// with every length scaled to the window, so shapes keep their size relative to the frame but are drawn
/// with the window's pixels. A window that is not 4:3 gets a wider (or taller) field rather than a
/// stretched one (<see cref="BatteryFieldScale"/>).
///
/// The split: <see cref="BatteryGpuEngine"/> runs the CPU port itself, so the presets, palettes, warp and
/// effect choices and every rand() draw are the original's. Its pixel stages are recorded and
/// <see cref="BatteryGlRenderer"/> replays them on the GPU. On any GL failure it logs to
/// <see cref="DataDirectory"/> and goes black, as the other GL plugins do.
///
/// Next / previous / random step through the 26 presets; preset 0 is "Randomization".
/// </summary>
public sealed class WmpBatteryVisualizer : OpenGlControlBase, IVisualizationController
{
    public static readonly StyledProperty<IAudioTap?> TapProperty =
        AvaloniaProperty.Register<WmpBatteryVisualizer, IAudioTap?>(nameof(Tap));

    public IAudioTap? Tap
    {
        get => GetValue(TapProperty);
        set => SetValue(TapProperty, value);
    }

    /// <summary>Private writable folder for the GL logs (host-supplied). When null, logging is skipped.</summary>
    public string? DataDirectory { get; set; }

    /// <summary>Raised (on the render thread) when the preset changes; carries its title.</summary>
    public event Action<string>? NameChanged;

    /// <summary>
    /// How long the window must hold one size before the field follows it. Every change of field size
    /// restarts the field (as a resolution change does in the original) and rebuilds the device tables, so
    /// a drag-resize would otherwise do that on every frame. Until then the old field is stretched.
    /// </summary>
    private static readonly TimeSpan ResizeSettle = TimeSpan.FromMilliseconds(200);

    /// <summary>Below this many device pixels on either axis the control is not rendered.</summary>
    private const int MinDeviceSize = 16;

    // Battery's creator does srand((unsigned)_time64(NULL)).
    private readonly BatteryGpuEngine _engine = new(seed: (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    private readonly TapToTimedLevel _adapter = new();
    private readonly Random _random = new();
    private DispatcherTimer? _pacer;
    private volatile int _requestedPreset = -1;
    private string _lastName = "";

    private BatteryGlRenderer? _renderer;
    private bool _failed;
    private bool _loggedRender;
    private BatteryFieldScale _pendingScale;
    private long _pendingSince;

    public void NextPreset() { _requestedPreset = (Pending + 1) % _engine.Core.PresetCount; RequestNextFrameRendering(); }

    public void PreviousPreset()
    {
        _requestedPreset = (Pending - 1 + _engine.Core.PresetCount) % _engine.Core.PresetCount;
        RequestNextFrameRendering();
    }

    public void RandomPreset() { _requestedPreset = _random.Next(_engine.Core.PresetCount); RequestNextFrameRendering(); }

    private int Pending => _requestedPreset >= 0 ? _requestedPreset : _engine.Core.CurrentPreset;

    /// <summary>
    /// Paces redraws at WMP's visualization rate with a timer. Every motion constant in the effect is
    /// per-frame, so the tick rate IS the animation speed.
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
            _renderer = new BatteryGlRenderer(new GlBindings(gl), isGles);
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
        catch (Exception ex) { _failed = true; LogFailure("render", ex); }
    }

    private void RenderFrame(BatteryGlRenderer renderer, int fb)
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var deviceW = Math.Max(1, (int)Math.Round(Bounds.Width * scaling));
        var deviceH = Math.Max(1, (int)Math.Round(Bounds.Height * scaling));

        // A collapsed control (the host lays it out at 1x1 before it is shown) would otherwise size the
        // field for a scale of ~0 and build device tables for it. Hold still until there is room.
        if (deviceW < MinDeviceSize || deviceH < MinDeviceSize) return;

        var scale = BatteryFieldScale.For(deviceW, deviceH);
        if (scale != renderer.Scale && SizeSettled(scale, first: renderer.Scale.DeviceWidth == 0))
        {
            renderer.EnsureSize(scale);
            _engine.Resize(scale);
            LogInfo($"field {scale.FieldWidth}x{scale.FieldHeight} at scale {scale.Scale:0.###} " +
                    $"-> {scale.DeviceWidth}x{scale.DeviceHeight}{(scale.IsExact ? " (exact)" : "")}");
        }

        var requested = _requestedPreset;
        if (requested >= 0)
        {
            _requestedPreset = -1;
            _engine.Core.SetCurrentPreset(requested);
        }

        var frame = _engine.Render(_adapter.Update(Tap));
        renderer.Step(frame, _engine.Tables);
        if (!_loggedRender)
        {
            _loggedRender = true;
            LogInfo($"first frame: preset '{_engine.Core.PresetTitle(_engine.Core.CurrentPreset)}', " +
                    $"{frame.Commands.Count} steps, {frame.VertexCount} vertices");
        }

        renderer.Present(fb, deviceW, deviceH);
        RaiseNameIfChanged();
    }

    /// <summary>True once <paramref name="scale"/> has been asked for continuously for
    /// <see cref="ResizeSettle"/> (or at once for the first size).</summary>
    private bool SizeSettled(BatteryFieldScale scale, bool first)
    {
        var now = Stopwatch.GetTimestamp();
        if (scale != _pendingScale)
        {
            _pendingScale = scale;
            _pendingSince = now;
        }
        return first || Stopwatch.GetElapsedTime(_pendingSince, now) >= ResizeSettle;
    }

    private void RaiseNameIfChanged()
    {
        var name = _engine.Core.PresetTitle(_engine.Core.CurrentPreset);
        if (name == _lastName) return;
        _lastName = name;
        NameChanged?.Invoke(name);
    }

    private void LogFailure(string phase, Exception ex)
    {
        Debug.WriteLine($"WMP Battery GL {phase} failed: {ex}");
        try
        {
            if (string.IsNullOrEmpty(DataDirectory)) return;
            File.AppendAllText(Path.Combine(DataDirectory, "wmp-battery-error.log"),
                               $"[{DateTime.Now:O}] WMP Battery GL {phase} failed:\n{ex}\n\n");
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// One-shot milestones. A black visualization with no failure log is ambiguous (GL never ran, or ran
    /// and drew nothing), and these tell the two apart without growing the log in steady state.
    /// </summary>
    private void LogInfo(string message)
    {
        Debug.WriteLine($"WMP Battery: {message}");
        try
        {
            if (string.IsNullOrEmpty(DataDirectory)) return;
            File.AppendAllText(Path.Combine(DataDirectory, "wmp-battery.log"), $"[{DateTime.Now:O}] {message}\n");
        }
        catch { /* ignore */ }
    }
}
