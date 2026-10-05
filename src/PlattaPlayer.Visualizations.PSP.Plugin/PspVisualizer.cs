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
using PlattaPlayer.Visualizations.PSP.Common;

namespace PlattaPlayer.Visualizations.PSP.Plugin;

/// <summary>
/// Runs the PSP visualizers (<see cref="PspVisualizerHost"/>) and replays each frame's GU commands with
/// <see cref="GuGlRenderer"/>. Frames are paced at the XMB's 60 Hz: all motion in the originals is per
/// frame, so the tick rate is the animation speed.
///
/// Nothing renders until <see cref="Assets"/> is set (the textures and JPEGs come from the user's firmware
/// and are extracted in the background); until then the surface is black and the host shows a status line.
/// Next / previous / random step through the visualizers in <see cref="PspVisualizerCatalog"/>.
/// </summary>
public sealed class PspVisualizer : OpenGlControlBase, IVisualizationController, IVisualizerHealth
{
    public static readonly StyledProperty<IAudioTap?> TapProperty =
        AvaloniaProperty.Register<PspVisualizer, IAudioTap?>(nameof(Tap));

    private const int MinDeviceSize = 16;

    private readonly float[] _left = new float[4096];
    private readonly float[] _right = new float[4096];
    private readonly Random _random = new();
    private volatile IPspAssets? _assets;
    private volatile int _requested = -1;
    private PspVisualizerHost? _host;
    private GuGlRenderer? _renderer;
    private DispatcherTimer? _pacer;
    private bool _failed;
    private long _heartbeat;
    private bool _loggedFirstFrame;

    public IAudioTap? Tap
    {
        get => GetValue(TapProperty);
        set => SetValue(TapProperty, value);
    }

    /// <summary>Private writable folder for the GL logs (host-supplied). When null, logging is skipped.</summary>
    public string? DataDirectory { get; set; }

    /// <summary>The extracted firmware resources. Set (from any thread) once loading has finished.</summary>
    public IPspAssets? Assets
    {
        get => _assets;
        set
        {
            _assets = value;
            Dispatcher.UIThread.Post(RequestNextFrameRendering);
        }
    }

    /// <summary>Raised (on the render thread) when the visualizer changes; carries its name.</summary>
    public event Action<string>? NameChanged;

    public void NextPreset() => _requested = (Pending + 1) % PspVisualizerCatalog.All.Count;

    public void PreviousPreset() =>
        _requested = (Pending - 1 + PspVisualizerCatalog.All.Count) % PspVisualizerCatalog.All.Count;

    public void RandomPreset() => _requested = _random.Next(PspVisualizerCatalog.All.Count);

    private int Pending => _requested >= 0 ? _requested : CurrentIndex;

    public long Heartbeat => Interlocked.Read(ref _heartbeat);

    public void Resume()
    {
        if (_pacer is { IsEnabled: false }) _pacer.Start();
        RequestNextFrameRendering();
    }

    private int CurrentIndex => _host?.Current is { } info ? IndexOf(info) : 0;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _pacer = new DispatcherTimer { Interval = PspVisualizerHost.FrameInterval };
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
            _renderer = new GuGlRenderer(new GlBindings(gl), isGles);
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
        _host?.Dispose();
        _host = null;
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_failed || _renderer is null) return;
        try { RenderFrame(_renderer, fb); }
        catch (Exception ex) { _failed = true; LogFailure("render", ex); return; }
        Interlocked.Increment(ref _heartbeat);
    }

    private void RenderFrame(GuGlRenderer renderer, int fb)
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var deviceW = Math.Max(1, (int)Math.Round(Bounds.Width * scaling));
        var deviceH = Math.Max(1, (int)Math.Round(Bounds.Height * scaling));
        if (deviceW < MinDeviceSize || deviceH < MinDeviceSize) return;

        if (_host is null)
        {
            if (_assets is not { } assets) return;
            _host = new PspVisualizerHost(assets);
            if (_requested < 0) _requested = LoadSelection();
        }

        var requested = _requested;
        if (requested >= 0)
        {
            _requested = -1;
            var info = PspVisualizerCatalog.All[requested];
            _host.Select(info);
            renderer.ResetTextures();
            LogInfo($"selected type {info.Type} ({info.Name})");
            SaveSelection(info);
            NameChanged?.Invoke(info.Name);
        }

        var frames = 0;
        if (Tap is { IsActive: true } tap) frames = tap.ReadStereoWaveform(_left, _right);
        _host.RenderFrame(_left, _right, frames);
        renderer.Render(_host.Frame, fb, deviceW, deviceH);

        if (!_loggedFirstFrame)
        {
            _loggedFirstFrame = true;
            LogInfo($"first frame: {_host.Frame.Commands.Count} commands, {_host.Frame.VertexCount} vertices");
        }
    }

    private string? SelectionPath => string.IsNullOrEmpty(DataDirectory) ? null : Path.Combine(DataDirectory, "selected-type.txt");

    /// <summary>The visualizer shown last time (by PSP type id), or the first one.</summary>
    private int LoadSelection()
    {
        try
        {
            if (SelectionPath is { } path && File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var type))
            {
                for (var i = 0; i < PspVisualizerCatalog.All.Count; i++)
                    if (PspVisualizerCatalog.All[i].Type == type) return i;
            }
        }
        catch { /* ignore */ }
        return 0;
    }

    private void SaveSelection(PspVisualizerInfo info)
    {
        try
        {
            if (SelectionPath is { } path) File.WriteAllText(path, info.Type.ToString());
        }
        catch { /* ignore */ }
    }

    private static int IndexOf(PspVisualizerInfo info)
    {
        for (var i = 0; i < PspVisualizerCatalog.All.Count; i++)
            if (PspVisualizerCatalog.All[i] == info) return i;
        return 0;
    }

    private void LogFailure(string phase, Exception ex)
    {
        Debug.WriteLine($"PSP visualizer GL {phase} failed: {ex}");
        try
        {
            if (string.IsNullOrEmpty(DataDirectory)) return;
            File.AppendAllText(Path.Combine(DataDirectory, "psp-error.log"),
                               $"[{DateTime.Now:O}] PSP visualizer GL {phase} failed:\n{ex}\n\n");
        }
        catch { /* ignore */ }
    }

    /// <summary>One-shot milestones, so a black surface with no failure log can be told apart.</summary>
    private void LogInfo(string message)
    {
        Debug.WriteLine($"PSP visualizer: {message}");
        try
        {
            if (string.IsNullOrEmpty(DataDirectory)) return;
            File.AppendAllText(Path.Combine(DataDirectory, "psp.log"), $"[{DateTime.Now:O}] {message}\n");
        }
        catch { /* ignore */ }
    }
}
