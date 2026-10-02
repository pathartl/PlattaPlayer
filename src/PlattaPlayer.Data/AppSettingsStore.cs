using System.Text.Json;
using System.Text.Json.Serialization;
using PlattaPlayer.Core.Abstractions;

namespace PlattaPlayer.Data;

/// <summary>
/// JSON-file-backed <see cref="IAppSettings"/>. Loaded once at construction; writes are flushed on
/// <see cref="Save"/>. Missing/corrupt files fall back to defaults rather than throwing.
/// </summary>
public sealed class AppSettingsStore : IAppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _path;
    private readonly Lock _gate = new();
    private Model _model;

    public AppSettingsStore() : this(AppPaths.SettingsPath) { }

    public AppSettingsStore(string path)
    {
        _path = path;
        _model = Load(path);
    }

    public string? GetCodecSetting(string codecId, string key)
    {
        lock (_gate)
            return _model.CodecSettings.TryGetValue(codecId, out var values) && values.TryGetValue(key, out var value)
                ? value
                : null;
    }

    public void SetCodecSetting(string codecId, string key, string? value)
    {
        lock (_gate)
        {
            if (!_model.CodecSettings.TryGetValue(codecId, out var values))
            {
                if (value is null) return;
                _model.CodecSettings[codecId] = values = new Dictionary<string, string>();
            }
            if (value is null) values.Remove(key);
            else values[key] = value;
            if (values.Count == 0) _model.CodecSettings.Remove(codecId);
        }
    }

    public string? ActiveVisualizerId
    {
        get { lock (_gate) return _model.ActiveVisualizerId; }
        set { lock (_gate) _model.ActiveVisualizerId = value; }
    }

    public bool ShowRemoteWaveforms
    {
        get { lock (_gate) return _model.ShowRemoteWaveforms; }
        set { lock (_gate) _model.ShowRemoteWaveforms = value; }
    }

    public string? AccentColor
    {
        get { lock (_gate) return _model.AccentColor; }
        set { lock (_gate) _model.AccentColor = value; }
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                var json = JsonSerializer.Serialize(_model, JsonOptions);
                File.WriteAllText(_path, json);
            }
            catch
            {
                // Best-effort persistence; a failure to write preferences must not crash the app.
            }
        }
    }

    private static Model Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return Migrate(JsonSerializer.Deserialize<Model>(File.ReadAllText(path)) ?? new Model());
        }
        catch
        {
            // Corrupt settings file — start from defaults.
        }
        return new Model();
    }

    /// <summary>The MIDI device used to be an app setting; it is now the MIDI codec plugin's "device".</summary>
    private static Model Migrate(Model model)
    {
        model.CodecSettings ??= new();
        if (model.MidiDeviceId is { } device)
        {
            if (!model.CodecSettings.TryGetValue("midi", out var midi))
                model.CodecSettings["midi"] = midi = new Dictionary<string, string>();
            midi.TryAdd("device", device);
            model.MidiDeviceId = null;
        }
        return model;
    }

    private sealed class Model
    {
        /// <summary>Legacy; read only to migrate it into <see cref="CodecSettings"/>.</summary>
        public string? MidiDeviceId { get; set; }

        /// <summary>Codec plugin settings: plugin id → key → value.</summary>
        public Dictionary<string, Dictionary<string, string>> CodecSettings { get; set; } = new();

        public string? ActiveVisualizerId { get; set; }
        public bool ShowRemoteWaveforms { get; set; } = true;
        public string? AccentColor { get; set; }
    }
}
