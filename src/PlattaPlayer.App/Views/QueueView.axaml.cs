using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PlattaPlayer.App.ViewModels;
using PlattaPlayer.App.ViewModels.Items;

namespace PlattaPlayer.App.Views;

/// <summary>
/// The queue panel. Code-behind handles the presentation-only behaviour: drag-to-reorder (press a row, drag
/// past a small threshold, an accent marker shows the drop slot, the list auto-scrolls near its edges),
/// keyboard editing, the row context menu, and bringing the playing row into view.
/// </summary>
public partial class QueueView : UserControl
{
    private const double DragThreshold = 5;
    private const double AutoScrollZone = 44;
    private const double AutoScrollMaxStep = 18;

    private readonly DispatcherTimer _autoScroll;
    private MainWindowViewModel? _vm;

    // Drag state: the row under a left press (not yet a drag), then the row being dragged.
    private QueueItemViewModel? _pressed;
    private Point _pressPoint;
    private QueueItemViewModel? _dragging;
    private int _dropIndex = -1;    // insertion slot 0..Count: the dragged row lands before this row
    private Point _lastPoint;       // relative to QueueList

    public QueueView()
    {
        InitializeComponent();

        _autoScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _autoScroll.Tick += (_, _) => AutoScrollStep();

        // Tunnel the press so it's seen before the row's selection handling; nothing here marks it handled,
        // so click-to-select and double-click-to-play keep working.
        QueueList.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        QueueList.AddHandler(PointerMovedEvent, OnListPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        QueueList.AddHandler(PointerReleasedEvent, OnListPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        // Only the list's own capture ending cancels a drag; taking capture makes the row that held it
        // report a loss too, which bubbles up here.
        QueueList.AddHandler(PointerCaptureLostEvent, (_, e) =>
        {
            if (ReferenceEquals(e.Source, QueueList)) EndDrag(commit: false);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        QueueList.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
    }

    private QueueViewModel? Queue => _vm?.Queue;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnShellChanged;
            _vm.Queue.PropertyChanged -= OnQueueChanged;
        }
        _vm = DataContext as MainWindowViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnShellChanged;
            _vm.Queue.PropertyChanged += OnQueueChanged;
        }
    }

    // Opening the panel, or starting a new queue, brings the playing row into view.
    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsQueueOpen) && _vm?.IsQueueOpen == true)
            Dispatcher.UIThread.Post(ScrollToCurrent, DispatcherPriority.Loaded);
    }

    private void OnQueueChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QueueViewModel.Items))
            Dispatcher.UIThread.Post(ScrollToCurrent, DispatcherPriority.Loaded);
    }

    private void ScrollToCurrent()
    {
        if (Queue is { CurrentIndex: >= 0 } q && IsEffectivelyVisible && _dragging is null)
            QueueList.ScrollIntoView(q.CurrentIndex);
    }

    // ---- Drag to reorder ------------------------------------------------------------------------------

    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressed = null;
        if (!e.GetCurrentPoint(QueueList).Properties.IsLeftButtonPressed) return;

        // Buttons in the row (remove) keep their own click.
        if (e.Source is not Visual source || source.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        if (source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is not QueueItemViewModel item) return;

        _pressed = item;
        _pressPoint = e.GetPosition(QueueList);
    }

    private void OnListPointerMoved(object? sender, PointerEventArgs e)
    {
        var point = e.GetPosition(QueueList);

        if (_dragging is null)
        {
            if (_pressed is null) return;
            var d = point - _pressPoint;
            if (Math.Abs(d.X) < DragThreshold && Math.Abs(d.Y) < DragThreshold) return;

            _dragging = _pressed;
            _dragging.IsDragging = true;
            QueueList.SelectedItem = _dragging;
            e.Pointer.Capture(QueueList);
            _autoScroll.Start();
        }

        _lastPoint = point;
        UpdateDropMarker();
        e.Handled = true;
    }

    private void OnListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _pressed = null;
        if (_dragging is null) return;

        EndDrag(commit: true);
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void EndDrag(bool commit)
    {
        if (_dragging is not { } item) return;
        _dragging = null;
        _pressed = null;
        _autoScroll.Stop();
        DropMarker.IsVisible = false;
        item.IsDragging = false;

        if (!commit || Queue is not { } queue || _dropIndex < 0) return;
        var from = queue.Items.IndexOf(item);
        if (from < 0) return;   // removed mid-drag
        var to = _dropIndex > from ? _dropIndex - 1 : _dropIndex;
        queue.Move(from, to);
        QueueList.SelectedItem = item;
    }

    /// <summary>
    /// Finds the insertion slot under the pointer from the realized rows (the pointer is always over the
    /// viewport, so the rows around it are realized) and draws the marker on the boundary between rows.
    /// </summary>
    private void UpdateDropMarker()
    {
        if (_dragging is null || Queue is not { } queue) return;

        var rows = QueueList.GetRealizedContainers()
            .Where(c => c.IsVisible)
            .Select(c => (Index: QueueList.IndexFromContainer(c), Top: c.TranslatePoint(default, QueueList)?.Y, c.Bounds.Height))
            .Where(r => r.Index >= 0 && r.Top is not null)
            .OrderBy(r => r.Index)
            .ToList();
        if (rows.Count == 0) return;

        var y = Math.Clamp(_lastPoint.Y, 0, QueueList.Bounds.Height);
        var slot = rows[^1].Index + 1;
        var markerY = rows[^1].Top!.Value + rows[^1].Height;
        foreach (var r in rows)
        {
            if (y < r.Top!.Value + r.Height / 2)
            {
                slot = r.Index;
                markerY = r.Top.Value;
                break;
            }
        }

        _dropIndex = slot;
        var from = queue.Items.IndexOf(_dragging);
        var changesOrder = slot != from && slot != from + 1;
        DropMarker.IsVisible = changesOrder;
        if (!changesOrder) return;

        DropMarker.Width = Math.Max(0, QueueList.Bounds.Width - 12);
        Canvas.SetLeft(DropMarker, 6);
        Canvas.SetTop(DropMarker, Math.Clamp(markerY, 0, QueueList.Bounds.Height) - 1);
    }

    // Scrolls while the pointer is held near the top or bottom edge, faster the closer it gets.
    private void AutoScrollStep()
    {
        if (_dragging is null || QueueList.FindDescendantOfType<ScrollViewer>() is not { } scroller) return;

        var h = QueueList.Bounds.Height;
        double step = 0;
        if (_lastPoint.Y < AutoScrollZone)
            step = -AutoScrollMaxStep * Math.Min(1, (AutoScrollZone - _lastPoint.Y) / AutoScrollZone);
        else if (_lastPoint.Y > h - AutoScrollZone)
            step = AutoScrollMaxStep * Math.Min(1, (_lastPoint.Y - (h - AutoScrollZone)) / AutoScrollZone);
        if (step == 0) return;

        var max = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
        var y = Math.Clamp(scroller.Offset.Y + step, 0, max);
        if (Math.Abs(y - scroller.Offset.Y) < 0.5) return;
        scroller.Offset = scroller.Offset.WithY(y);
        // Re-measure the marker once the rows have moved.
        Dispatcher.UIThread.Post(UpdateDropMarker, DispatcherPriority.Loaded);
    }

    // ---- Keyboard, double-click, context menu ---------------------------------------------------------

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (Queue is not { } queue || QueueList.SelectedItem is not QueueItemViewModel item) return;
        var index = queue.Items.IndexOf(item);

        switch (e.Key)
        {
            case Key.Delete when e.KeyModifiers == KeyModifiers.None:
                queue.RemoveCommand.Execute(item);
                // Keep a row selected so repeated Delete walks down the list.
                if (queue.Items.Count > 0)
                    Dispatcher.UIThread.Post(() => QueueList.SelectedIndex = Math.Min(index, queue.Items.Count - 1));
                break;
            case Key.Enter when e.KeyModifiers == KeyModifiers.None:
                queue.PlayCommand.Execute(item);
                break;
            case Key.Up when e.KeyModifiers == KeyModifiers.Alt && index > 0:
                MoveSelected(queue, item, index, index - 1);
                break;
            case Key.Down when e.KeyModifiers == KeyModifiers.Alt && index < queue.Items.Count - 1:
                MoveSelected(queue, item, index, index + 1);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void MoveSelected(QueueViewModel queue, QueueItemViewModel item, int from, int to)
    {
        queue.Move(from, to);
        QueueList.SelectedItem = item;
        QueueList.ScrollIntoView(to);
    }

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is QueueItemViewModel item)
            Queue?.PlayCommand.Execute(item);
    }

    private void OnRowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: QueueItemViewModel item } anchor || _vm is not { } shell) return;
        var queue = shell.Queue;
        var index = queue.Items.IndexOf(item);

        var menu = new ContextMenu();
        void Add(string header, Action action, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }

        Add("Play", () => queue.PlayCommand.Execute(item), enabled: !item.IsCurrent);
        Add("Play next", () => queue.MoveToNextCommand.Execute(item),
            enabled: !item.IsCurrent && index != queue.CurrentIndex + 1);
        Add("Remove from queue", () => queue.RemoveCommand.Execute(item), enabled: !item.IsCurrent);
        menu.Items.Add(new Separator());
        Add("Go to album", () => shell.GoToAlbumCommand.Execute(item.Track.AlbumId), enabled: item.Track.AlbumId > 0);
        Add("Go to artist", () => shell.GoToArtistCommand.Execute(item.Track.ArtistId), enabled: item.Track.ArtistId > 0);

        menu.Open(anchor);
        e.Handled = true;
    }
}
