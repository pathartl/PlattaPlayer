using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlattaPlayer.Codecs.Abstractions;
using PlattaPlayer.Core.Abstractions;

namespace PlattaPlayer.App.ViewModels.Items;

/// <summary>A codec plugin's setting on the Settings page (e.g. the MIDI codec's default device). The pick is
/// stored in the app settings, where the plugin reads it through its host.</summary>
public sealed partial class CodecSettingViewModel : ObservableObject
{
    private readonly ICodecPlugin _plugin;
    private readonly CodecSetting _setting;
    private readonly IAppSettings _settings;
    private bool _loading;

    public CodecSettingViewModel(ICodecPlugin plugin, CodecSetting setting, IAppSettings settings)
    {
        _plugin = plugin;
        _setting = setting;
        _settings = settings;
        Refresh();
    }

    /// <summary>The section heading: the plugin's name.</summary>
    public string Title => _plugin.DisplayName;

    public string Label => _setting.Label;
    public string? Description => _setting.Description;
    public bool HasDescription => !string.IsNullOrEmpty(Description);
    public IReadOnlyList<CodecFolder> Folders => _setting.Folders;

    public ObservableCollection<CodecChoice> Choices { get; } = new();

    [ObservableProperty] private CodecChoice? _selected;

    partial void OnSelectedChanged(CodecChoice? value)
    {
        if (_loading || value is null) return;
        _settings.SetCodecSetting(_plugin.Id, _setting.Key, value.Value);
        _settings.Save();
    }

    /// <summary>Re-reads the choices (e.g. after the user dropped in a new SoundFont or ROM).</summary>
    [RelayCommand]
    private void Refresh()
    {
        _loading = true;
        try
        {
            Choices.Clear();
            try
            {
                foreach (var choice in _setting.GetChoices())
                    Choices.Add(choice);
            }
            catch
            {
                // A failing plugin offers nothing rather than breaking the Settings page.
            }

            // Shows the value in effect without storing it: an unpicked setting keeps following the default.
            string? effective;
            try { effective = _setting.GetEffective(); }
            catch { effective = null; }
            Selected = Choices.FirstOrDefault(c => c.Value == effective);
        }
        finally
        {
            _loading = false;
        }
    }
}
