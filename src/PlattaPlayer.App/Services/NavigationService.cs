using System;
using System.Collections.Generic;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using PlattaPlayer.App.ViewModels;

namespace PlattaPlayer.App.Services;

/// <summary>
/// Default navigation service. Each navigation creates a fresh page instance from DI; previous pages are kept
/// on a bounded back stack so going back restores them as they were (scroll position aside).
/// </summary>
public sealed class NavigationService : INavigationService
{
    private const int MaxHistory = 50;

    private readonly IServiceProvider _services;
    private readonly LinkedList<PageViewModelBase> _history = new();

    public NavigationService(IServiceProvider services) => _services = services;

    public PageViewModelBase? CurrentPage { get; private set; }

    public bool CanGoBack => _history.Count > 0;

    public event Action? CurrentPageChanged;

    public void NavigateTo<TPage>(Action<TPage>? configure = null) where TPage : PageViewModelBase
    {
        var page = _services.GetRequiredService<TPage>();
        configure?.Invoke(page);

        if (CurrentPage is not null)
        {
            _history.AddLast(CurrentPage);
            if (_history.Count > MaxHistory) _history.RemoveFirst();
        }

        CurrentPage = page;
        CurrentPageChanged?.Invoke();

        // Fire-and-forget load on the UI thread; pages flip IsBusy while loading.
        Dispatcher.UIThread.Post(async () => await page.InitializeAsync());
    }

    public void GoBack()
    {
        if (_history.Last is not { } previous) return;
        _history.RemoveLast();
        CurrentPage = previous.Value;
        CurrentPageChanged?.Invoke();
    }
}
