using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PlattaPlayer.App.ViewModels;

/// <summary>One preset accent color in the Settings picker.</summary>
public sealed partial class AccentSwatch(string name, Color color) : ObservableObject
{
    public string Name { get; } = name;

    public Color Color { get; } = color;

    public IBrush Brush { get; } = new SolidColorBrush(color);

    [ObservableProperty] private bool _isSelected;
}
