using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using PlattaPlayer.Codecs.Abstractions;

namespace PlattaPlayer.App.ViewModels.Items;

/// <summary>A format-specific tag field in the Tag Editor panel (e.g. an SPC's fade length, or the SoundFont a
/// MIDI file plays with), editing the selected files' value for one codec tag key. Typed as text, or picked
/// from a list for a choice field.</summary>
public sealed partial class FormatFieldViewModel : ObservableObject
{
    // A choice field's value while the selected files' values differ (shown as no choice in the list).
    private const string Mixed = "\0mixed";

    private readonly Action<FormatFieldViewModel> _changed;
    private bool _loading;

    public FormatFieldViewModel(CodecTagField field, Action<FormatFieldViewModel> changed)
    {
        Key = field.Key;
        Label = field.Label;
        Hint = field.Hint;
        _changed = changed;

        if (field is { Kind: CodecTagFieldKind.Choice, Choices: { } choices })
        {
            IsChoice = true;
            try
            {
                foreach (var choice in choices())
                    Options.Add(choice);
            }
            catch
            {
                // The plugin couldn't list its choices: only the values already in the files are offered.
            }
        }
    }

    public string Key { get; }
    public string Label { get; }
    public string? Hint { get; }
    public bool HasHint => !string.IsNullOrEmpty(Hint);

    /// <summary>Picked from <see cref="Options"/> rather than typed.</summary>
    public bool IsChoice { get; }
    public bool IsText => !IsChoice;

    public ObservableCollection<CodecChoice> Options { get; } = new();

    /// <summary>The value shared by the selection ("" when unset or, for a text field, when the files differ).</summary>
    [ObservableProperty] private string _value = string.Empty;

    /// <summary>"Mixed" while the selected files' values differ.</summary>
    [ObservableProperty] private string _placeholder = "—";

    /// <summary>The value as loaded from the selection, to tell an edit from putting a field back.</summary>
    public string LoadedValue { get; private set; } = string.Empty;

    /// <summary>The <see cref="Options"/> entry for a choice field's value; null while the selection's values
    /// differ. A value not in the list (e.g. a SoundFont that has gone missing) is added to it, so it still shows.</summary>
    public CodecChoice? SelectedOption
    {
        get => Value == Mixed ? null : Options.FirstOrDefault(o => string.Equals(o.Value, Value, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is not null && !string.Equals(value.Value, Value, StringComparison.OrdinalIgnoreCase))
                Value = value.Value;
        }
    }

    /// <summary>Shows the selection's value without it counting as an edit.</summary>
    public void Load(IReadOnlyCollection<string> values)
    {
        _loading = true;
        var mixed = values.Count > 1;
        Value = mixed ? (IsChoice ? Mixed : string.Empty) : values.FirstOrDefault() ?? string.Empty;
        LoadedValue = Value;
        Placeholder = mixed ? "Mixed" : "—";
        if (IsChoice && Value != Mixed && !Options.Any(o => string.Equals(o.Value, Value, StringComparison.OrdinalIgnoreCase)))
            Options.Add(new CodecChoice(Value, $"{Value} (not found)"));
        OnPropertyChanged(nameof(SelectedOption));
        _loading = false;
    }

    partial void OnValueChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedOption));
        if (!_loading) _changed(this);
    }
}
