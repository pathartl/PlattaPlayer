using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using PlattaPlayer.App.ViewModels;
using PlattaPlayer.App.Visualizations;
using PlattaPlayer.Visualizations.Abstractions;

namespace PlattaPlayer.App;

public partial class MainWindow : Window
{
    private VisualizerInstance? _visualizer;
    private readonly VisualizerWatchdog _watchdog;
    private WindowState _stateBeforeFullScreen = WindowState.Normal;

    public MainWindow()
    {
        InitializeComponent();

        // The caption buttons carry WindowDecorationProperties.ElementRole, so Windows hit-tests them natively
        // (Snap Layouts on Maximize, drag/double-click on the title bar). The click handlers cover platforms
        // that don't route those roles through the OS.
        MinimizeButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaximizeButton.Click += (_, _) => ToggleMaximize();
        CloseButton.Click += (_, _) => Close();

        // Tunnel so global shortcuts (Space, Ctrl+…) win over focused buttons, but text fields keep typing keys.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        _watchdog = new VisualizerWatchdog(IsVisualizerRendering, () =>
        {
            if (ViewModel is { } vm) BuildVisualizer(vm, recovery: true);
        });

        UpdateChromeForWindowState();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
            UpdateChromeForWindowState();
    }

    // Maximized/fullscreen windows are edge-to-edge, so drop the hairline outline there, swap the
    // Maximize glyph for Restore, and stop the visualizer rendering while minimized.
    private void UpdateChromeForWindowState()
    {
        var edgeToEdge = WindowState is WindowState.Maximized or WindowState.FullScreen;
        RootBorder.BorderThickness = new Thickness(edgeToEdge ? 0 : 1);

        var maximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Data = (Geometry)this.FindResource(maximized ? "CaptionRestore" : "CaptionMaximize")!;
        ToolTip.SetTip(MaximizeButton, maximized ? "Restore" : "Maximize");
        AutomationProperties.SetName(MaximizeButton, maximized ? "Restore" : "Maximize");

        if (_visualizer is null) return;
        var minimized = WindowState == WindowState.Minimized;
        _visualizer.View.IsVisible = !minimized;
        // Coming back from minimized is where render loops lose their pending frame request.
        if (!minimized) _watchdog.Resume();
    }

    // On screen with room to draw: the only time a stopped heartbeat means the visualizer is stuck.
    private bool IsVisualizerRendering()
        => _visualizer?.View is { IsEffectivelyVisible: true, Bounds: { Width: > 0, Height: > 0 } }
           && WindowState != WindowState.Minimized;

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        // DataContext is assigned after construction, so build the active visualizer here and rebuild it
        // whenever the choice changes.
        if (DataContext is MainWindowViewModel vm)
        {
            vm.Visualization.PropertyChanged += OnVisualizationChanged;
            vm.Visualization.RestartRequested += (_, _) => BuildVisualizer(vm);
            BuildVisualizer(vm);
        }
    }

    private void OnVisualizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VisualizationSettingsViewModel.ActiveVisualizer)
            && DataContext is MainWindowViewModel vm)
            BuildVisualizer(vm);
    }

    // Creates the active plugin's control and swaps it into the host panel. Removing the old control from
    // the visual tree lets it tear down its render loop / GPU resources. Also how a stuck visualizer is
    // restarted, by the watchdog (recovery) or the user.
    private void BuildVisualizer(MainWindowViewModel vm, bool recovery = false)
    {
        VisualizerHost.Children.Clear();
        _visualizer = null;
        vm.Visualization.Controller = null;

        var plugin = vm.Visualization.ActiveVisualizer;
        if (plugin is null)
        {
            _watchdog.Attach(null, "");
            return;
        }

        var context = new VisualizerHostContext
        {
            Tap = vm.NowPlaying.AudioTap,
            Settings = vm.Visualization,
            // The glass panels: plugins that support it paint a blurred copy of the visualization behind them.
            BlurTargets = new List<Visual> { NavRail, TransportBar, QueuePanel.GlassBackdrop },
            ReportName = name => Dispatcher.UIThread.Post(() => vm.Visualization.CurrentPresetName = name),
            // Private per-plugin data folder (presets, caches, error logs).
            DataDirectory = PlattaPlayer.Data.AppPaths.PluginDataDirectory(plugin.Id),
            // User-supplied firmware dumps (shared with the emulated MIDI modules).
            RomsDirectory = PlattaPlayer.Data.AppPaths.RomsDirectory,
        };

        _visualizer = plugin.Create(context);
        _visualizer.View.IsVisible = WindowState != WindowState.Minimized;
        VisualizerHost.Children.Add(_visualizer.View);
        vm.Visualization.Controller = _visualizer.Controller;
        _watchdog.Attach(_visualizer, plugin.DisplayName, recovery);
    }

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void ToggleFullScreen()
    {
        if (WindowState == WindowState.FullScreen)
            WindowState = _stateBeforeFullScreen;
        else
        {
            _stateBeforeFullScreen = WindowState;
            WindowState = WindowState.FullScreen;
        }
    }

    /// <summary>Focuses the nav-rail search field (Ctrl+F), leaving Now Playing if needed.</summary>
    private void FocusSearch()
    {
        ViewModel?.CloseNowPlayingCommand.Execute(null);
        Dispatcher.UIThread.Post(() => { SearchBox.Focus(); SearchBox.SelectAll(); }, DispatcherPriority.Input);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        var typing = FocusManager?.GetFocusedElement() is TextBox;
        var ctrl = e.KeyModifiers == KeyModifiers.Control;
        var none = e.KeyModifiers == KeyModifiers.None;

        switch (e.Key)
        {
            case Key.F11 when none:
                ToggleFullScreen();
                break;
            case Key.Escape when none && WindowState == WindowState.FullScreen:
                WindowState = _stateBeforeFullScreen;
                break;
            case Key.Escape when none && vm.IsQueueOpen:
                vm.CloseQueueCommand.Execute(null);
                break;
            case Key.Space when none && !typing:
                vm.NowPlaying.PlayPauseCommand.Execute(null);
                break;
            case Key.Left when ctrl:
                vm.NowPlaying.PreviousCommand.Execute(null);
                break;
            case Key.Right when ctrl:
                vm.NowPlaying.NextCommand.Execute(null);
                break;
            case Key.F when ctrl:
                FocusSearch();
                break;
            case Key.L when ctrl:
                vm.ToggleLyricsCommand.Execute(null);
                break;
            case Key.Q when ctrl:
                vm.ToggleQueueCommand.Execute(null);
                break;
            case Key.R when ctrl:
                vm.Visualization.RestartVisualizerCommand.Execute(null);
                break;
            case Key.Enter when ctrl:
                if (vm.NowPlaying.HasTrack) vm.ToggleNowPlayingCommand.Execute(null);
                break;
            case Key.Left when e.KeyModifiers == KeyModifiers.Alt:
                vm.GoBackCommand.Execute(null);
                break;
            case Key.OemOpenBrackets when none && !typing:
                vm.Visualization.PreviousPresetCommand.Execute(null);
                break;
            case Key.OemCloseBrackets when none && !typing:
                vm.Visualization.NextPresetCommand.Execute(null);
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
