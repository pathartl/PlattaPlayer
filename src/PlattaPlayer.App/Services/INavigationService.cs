using System;
using PlattaPlayer.App.ViewModels;

namespace PlattaPlayer.App.Services;

/// <summary>Drives the content region by resolving and activating page view-models.</summary>
public interface INavigationService
{
    PageViewModelBase? CurrentPage { get; }

    /// <summary>True when there is a previous page to return to (Alt+←).</summary>
    bool CanGoBack { get; }

    event Action? CurrentPageChanged;

    /// <summary>Resolve <typeparamref name="TPage"/> from DI, optionally configure it, then activate it.</summary>
    void NavigateTo<TPage>(Action<TPage>? configure = null) where TPage : PageViewModelBase;

    /// <summary>Returns to the previous page, keeping its loaded state.</summary>
    void GoBack();
}
