using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PlattaPlayer.App.ViewModels;

namespace PlattaPlayer.App.Views;

/// <summary>
/// Now Playing: the visualizer has the stage. Code-behind handles the presentation-only behaviour: idle
/// mode (fade controls and hide the cursor after 3s without input), the responsive lyrics column width, and
/// smooth lyric auto-scroll that keeps the current line about a third of the way down, pausing for 5s
/// whenever the user scrolls the lyrics themselves.
/// </summary>
public partial class NowPlayingView : UserControl
{
    private static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ManualScrollHold = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ScrollDuration = TimeSpan.FromMilliseconds(450);

    private readonly DispatcherTimer _idleTimer;
    private readonly DispatcherTimer _scrollTimer;
    private MainWindowViewModel? _vm;
    private DateTime _manualScrollAt = DateTime.MinValue;
    private double _scrollFrom, _scrollTo;
    private DateTime _scrollStart;

    public NowPlayingView()
    {
        InitializeComponent();

        _idleTimer = new DispatcherTimer { Interval = IdleAfter };
        _idleTimer.Tick += (_, _) => SetIdle(true);

        _scrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _scrollTimer.Tick += (_, _) => StepScroll();

        Root.PointerMoved += (_, _) => WakeUp();
        Root.PointerPressed += (_, _) => WakeUp();
        AddHandler(KeyDownEvent, (_, _) => WakeUp(), Avalonia.Interactivity.RoutingStrategies.Tunnel);

        LyricsScroll.PointerWheelChanged += (_, _) => _manualScrollAt = DateTime.UtcNow;
        Root.SizeChanged += (_, _) => UpdateLyricsWidth();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            if (IsVisible) { WakeUp(); Dispatcher.UIThread.Post(() => ScrollToCurrent(animate: false), DispatcherPriority.Loaded); }
            else { _idleTimer.Stop(); SetIdle(false); }
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.Lyrics.PropertyChanged -= OnLyricsChanged;
            _vm.PropertyChanged -= OnShellChanged;
        }
        _vm = DataContext as MainWindowViewModel;
        if (_vm is not null)
        {
            _vm.Lyrics.PropertyChanged += OnLyricsChanged;
            _vm.PropertyChanged += OnShellChanged;
        }
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsLyricsOpen) && _vm?.IsLyricsOpen == true)
            Dispatcher.UIThread.Post(() => ScrollToCurrent(animate: false), DispatcherPriority.Loaded);
    }

    private void OnLyricsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LyricsViewModel.CurrentIndex))
            // Let the line re-measure at its "current" size before scrolling to it.
            Dispatcher.UIThread.Post(() => ScrollToCurrent(animate: true), DispatcherPriority.Loaded);
        else if (e.PropertyName == nameof(LyricsViewModel.HasLyrics))
            LyricsScroll.Offset = default;
    }

    // ---- Idle mode --------------------------------------------------------------------------------

    private void WakeUp()
    {
        SetIdle(false);
        _idleTimer.Stop();
        if (IsVisible) _idleTimer.Start();
    }

    private void SetIdle(bool idle)
    {
        // Stay awake while a flyout (preset settings, More) is open.
        if (idle && TopLevel.GetTopLevel(this) is { } top && top.FocusManager?.GetFocusedElement() is Control { } c
            && c.FindAncestorOfType<Avalonia.Controls.Primitives.PopupRoot>() is not null)
            return;

        Root.Classes.Set("Idle", idle);
        Root.Cursor = idle ? new Cursor(StandardCursorType.None) : Cursor.Default;
        if (idle) _idleTimer.Stop();
    }

    // ---- Lyrics -----------------------------------------------------------------------------------

    private void UpdateLyricsWidth()
        => LyricsColumn.Width = Math.Min(600, Root.Bounds.Width * 0.44);

    private void ScrollToCurrent(bool animate)
    {
        if (_vm is null || !IsVisible || !_vm.IsLyricsOpen) return;
        if (DateTime.UtcNow - _manualScrollAt < ManualScrollHold) return;

        var index = _vm.Lyrics.CurrentIndex;
        if (index < 0) { AnimateTo(0, animate); return; }
        if (LyricsList.ContainerFromIndex(index) is not Control line) return;
        if (line.TranslatePoint(new Point(0, 0), LyricsList) is not { } top) return;

        // Put the current line about a third of the way down the column.
        var target = top.Y - LyricsScroll.Viewport.Height / 3;
        var max = Math.Max(0, LyricsScroll.Extent.Height - LyricsScroll.Viewport.Height);
        AnimateTo(Math.Clamp(target, 0, max), animate);
    }

    private void AnimateTo(double y, bool animate)
    {
        if (!animate)
        {
            _scrollTimer.Stop();
            LyricsScroll.Offset = new Vector(0, y);
            return;
        }
        _scrollFrom = LyricsScroll.Offset.Y;
        _scrollTo = y;
        _scrollStart = DateTime.UtcNow;
        _scrollTimer.Start();
    }

    private void StepScroll()
    {
        var t = Math.Clamp((DateTime.UtcNow - _scrollStart) / ScrollDuration, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3); // ease-out cubic
        LyricsScroll.Offset = new Vector(0, _scrollFrom + (_scrollTo - _scrollFrom) * eased);
        if (t >= 1) _scrollTimer.Stop();
    }
}
