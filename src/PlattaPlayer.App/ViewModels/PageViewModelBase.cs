using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PlattaPlayer.App.ViewModels;

/// <summary>Base class for top-level navigable pages shown in the content region.</summary>
public abstract partial class PageViewModelBase : ObservableObject
{
    /// <summary>Title shown in the page header.</summary>
    public abstract string Title { get; }

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Called once the page becomes the current page. Override to load data.</summary>
    public virtual Task InitializeAsync() => Task.CompletedTask;
}
