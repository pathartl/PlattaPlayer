using System;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using PlattaPlayer.App.ViewModels;

namespace PlattaPlayer.App;

/// <summary>
/// Maps a view-model instance to its view by naming convention
/// (e.g. <c>…ViewModels.AlbumsViewModel</c> → <c>…Views.AlbumsView</c>).
/// </summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control Build(object? data)
    {
        if (data is null)
            return new TextBlock { Text = "null" };

        var name = data.GetType().FullName!
            .Replace("ViewModels", "Views", StringComparison.Ordinal)
            .Replace("ViewModel", "View", StringComparison.Ordinal);

        var type = Type.GetType(name);
        if (type is not null)
            return (Control)Activator.CreateInstance(type)!;

        return new TextBlock { Text = "Not Found: " + name };
    }

    public bool Match(object? data) => data is PageViewModelBase;
}
