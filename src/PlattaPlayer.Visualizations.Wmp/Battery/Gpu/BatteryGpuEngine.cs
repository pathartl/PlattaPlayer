using PlattaPlayer.Visualizations.Wmp.Audio;

namespace PlattaPlayer.Visualizations.Wmp.Battery.Gpu;

/// <summary>
/// The CPU half of the window-resolution Battery. It holds a <see cref="BatteryCore"/> whose pixel stages
/// are a <see cref="BatteryFrameRecorder"/>, so every decision and every rand() draw is the CPU port's,
/// and each <see cref="Render(BatteryLevels)"/> yields a <see cref="BatteryGpuFrame"/> for the GL renderer
/// to replay. Away from scale 1 it also keeps the <see cref="DeviceShiftTables"/>.
/// </summary>
public sealed class BatteryGpuEngine : IDisposable
{
    private readonly BatteryLevels _levels = new();
    private BatteryFrameRecorder? _recorder;

    /// <param name="rand">The stream to draw from (the harness passes a seeded one shared with nothing).</param>
    /// <param name="seed">srand() after construction, as Battery's creator does; null leaves the stream as is.</param>
    public BatteryGpuEngine(CrtRand? rand = null, uint? seed = null, bool useTransitions = true)
    {
        Core = new BatteryCore(rand, seed, useTransitions, raster: rd => _recorder = new BatteryFrameRecorder(rd));
        Scale = BatteryFieldScale.For(BatteryCore.FieldWidth, BatteryCore.FieldHeight);
    }

    public BatteryCore Core { get; }

    public BatteryFieldScale Scale { get; private set; }

    /// <summary>The device tables; null at scale 1, where the GPU gathers through the field's own maps.</summary>
    public DeviceShiftTables? Tables { get; private set; }

    public BatteryGpuFrame Frame => _recorder!.Frame;

    /// <summary>Run the field at <paramref name="scale"/>'s field size. A change of size erases the field,
    /// as a resolution change does in the original.</summary>
    public void Resize(BatteryFieldScale scale)
    {
        if (scale == Scale && (Tables is not null || scale.IsExact)) return;
        Scale = scale;
        Core.Resize(scale.FieldWidth, scale.FieldHeight);
        _recorder!.ExactPixels = scale.IsExact;
        Tables?.Dispose();
        Tables = scale.IsExact ? null : new DeviceShiftTables(scale);
    }

    public BatteryGpuFrame Render(TimedLevelFrame frame)
    {
        _levels.LoadFrom(frame);
        return Render(_levels);
    }

    public BatteryGpuFrame Render(BatteryLevels levels)
    {
        var recorder = _recorder!;
        recorder.Begin();
        var frame = recorder.Frame;
        frame.Visible = Core.Render(levels);
        recorder.End();
        Core.PresentedPalette.CopyTo(frame.Palette, 0);
        frame.StopFill = Core.StopFillColor;
        Tables?.Poll(Core.Preset);
        return frame;
    }

    public void Dispose() => Tables?.Dispose();
}
