using PlattaPlayer.Codecs.Abstractions;
using PlattaPlayer.Core.Abstractions;
using PlattaPlayer.Data;

namespace PlattaPlayer.App.Plugins;

/// <summary>The app as seen by one codec plugin: its data folder, the shared drop-in folders and its settings.</summary>
internal sealed class CodecHost : ICodecHost
{
    private readonly string _codecId;
    private readonly IAppSettings _settings;

    public CodecHost(string codecId, IAppSettings settings)
    {
        _codecId = codecId;
        _settings = settings;
    }

    public string DataDirectory => AppPaths.PluginDataDirectory(_codecId);

    public string UserFolder(string name) => AppPaths.UserFolder(name);

    public string? GetSetting(string key) => _settings.GetCodecSetting(_codecId, key);
}
