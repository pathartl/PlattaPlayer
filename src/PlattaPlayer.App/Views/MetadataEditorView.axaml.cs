using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PlattaPlayer.App.ViewModels;
using PlattaPlayer.App.ViewModels.Items;

namespace PlattaPlayer.App.Views;

public partial class MetadataEditorView : UserControl
{
    // Marks an in-app row drag; the rows themselves are held in _draggedRows.
    private static readonly DataFormat<string> RowDragFormat =
        DataFormat.CreateInProcessFormat<string>("PlattaPlayer.TagEditor.Rows");

    private const double DragThreshold = 4;
    private const double AutoScrollMargin = 32;

    private readonly DataGrid _grid;
    private readonly Panel _gridPanel;
    private readonly Border _dropIndicator;

    private PointerPressedEventArgs? _pressArgs;
    private Point _pressPoint;
    private TagFileItemViewModel? _pressRow;
    private bool _deferredSelect;
    private List<TagFileItemViewModel>? _draggedRows;

    // Scrolls the grid a row at a time while a dragged row is held near its top or bottom edge.
    private readonly DispatcherTimer _autoScrollTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private int _autoScrollStep;

    public MetadataEditorView()
    {
        InitializeComponent();
        _grid = this.FindControl<DataGrid>("FilesGrid")!;
        _gridPanel = this.FindControl<Panel>("GridPanel")!;
        _dropIndicator = this.FindControl<Border>("DropIndicator")!;
        _autoScrollTimer.Tick += OnAutoScrollTick;

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // Tunnel, so a press on a selected row can be claimed before the grid collapses a multi-selection.
        _grid.AddHandler(PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel);
        _grid.AddHandler(PointerMovedEvent, OnGridPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        _grid.AddHandler(PointerReleasedEvent, OnGridPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private MetadataEditorViewModel? Vm => DataContext as MetadataEditorViewModel;

    // ===== Row drag: reorder the grid (the track order) =====

    private static DataGridRow? RowOf(object? source) =>
        (source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true);

    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressArgs = null;
        _deferredSelect = false;
        if (!e.GetCurrentPoint(_grid).Properties.IsLeftButtonPressed
            || RowOf(e.Source)?.DataContext is not TagFileItemViewModel item)
            return;

        _pressArgs = e;
        _pressPoint = e.GetPosition(_grid);
        _pressRow = item;

        // Pressing a row inside a multi-selection keeps the selection so the whole group can be dragged;
        // if no drag follows, the release selects just that row as a normal click would.
        if (e.KeyModifiers == KeyModifiers.None && _grid.SelectedItems.Count > 1 && _grid.SelectedItems.Contains(item))
        {
            _deferredSelect = true;
            e.Handled = true;
        }
    }

    private async void OnGridPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressArgs is not { } pressArgs || _pressRow is null || _draggedRows is not null)
            return;

        var p = e.GetPosition(_grid);
        if (Math.Abs(p.X - _pressPoint.X) < DragThreshold && Math.Abs(p.Y - _pressPoint.Y) < DragThreshold)
            return;

        var rows = _grid.SelectedItems.OfType<TagFileItemViewModel>().ToList();
        if (!rows.Contains(_pressRow))
            rows = new List<TagFileItemViewModel> { _pressRow };

        _pressArgs = null;
        _deferredSelect = false;
        _draggedRows = rows;
        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(RowDragFormat, "rows"));
            await DragDrop.DoDragDropAsync(pressArgs, data, DragDropEffects.Move);
        }
        finally
        {
            _draggedRows = null;
            _dropIndicator.IsVisible = false;
            StopAutoScroll();
        }
    }

    private void OnGridPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_deferredSelect && _pressRow is not null)
        {
            _grid.SelectedItems.Clear();
            _grid.SelectedItem = _pressRow;
        }
        _pressArgs = null;
        _deferredSelect = false;
    }

    /// <summary>Where a drop at the pointer would insert (an index into Files), plus the y (in the grid
    /// panel) to draw the insertion line at, or null when the target row isn't on screen.</summary>
    private (int Index, double? LineY) DropTarget(DragEventArgs e, MetadataEditorViewModel vm)
    {
        if (RowOf(e.Source) is { DataContext: TagFileItemViewModel item } row)
        {
            var after = e.GetPosition(row).Y > row.Bounds.Height / 2;
            var index = vm.Files.IndexOf(item) + (after ? 1 : 0);
            return (index, row.TranslatePoint(new Point(0, after ? row.Bounds.Height : 0), _gridPanel)?.Y);
        }

        // Over the column headers → the top; anywhere else (the empty space below the rows) → the end.
        var toTop = (e.Source as Visual)?.FindAncestorOfType<DataGridColumnHeader>(includeSelf: true) is not null;
        var edgeItem = vm.Files.Count == 0 ? null : toTop ? vm.Files[0] : vm.Files[^1];
        var edgeRow = edgeItem is null ? null : _grid.GetVisualDescendants().OfType<DataGridRow>()
            .FirstOrDefault(r => r.IsVisible && ReferenceEquals(r.DataContext, edgeItem));
        var y = edgeRow?.TranslatePoint(new Point(0, toTop ? 0 : edgeRow.Bounds.Height), _gridPanel)?.Y;
        return (toTop ? 0 : vm.Files.Count, y);
    }

    /// <summary>Starts, redirects or stops the edge auto-scroll for a drag at the pointer: within
    /// <see cref="AutoScrollMargin"/> of the top of the rows (below the headers) or of the bottom.</summary>
    private void UpdateAutoScroll(DragEventArgs e)
    {
        var y = e.GetPosition(_grid).Y;
        var top = HeaderHeight();
        _autoScrollStep = y < top + AutoScrollMargin ? -1 : y > _grid.Bounds.Height - AutoScrollMargin ? 1 : 0;
        if (_autoScrollStep == 0)
            _autoScrollTimer.Stop();
        else if (!_autoScrollTimer.IsEnabled)
        {
            OnAutoScrollTick(null, EventArgs.Empty);
            _autoScrollTimer.Start();
        }
    }

    private void StopAutoScroll()
    {
        _autoScrollStep = 0;
        _autoScrollTimer.Stop();
    }

    private double HeaderHeight() =>
        _grid.GetVisualDescendants().OfType<DataGridColumnHeadersPresenter>().FirstOrDefault()?.Bounds.Height ?? 0;

    /// <summary>Brings the row just past the first/last fully visible one into view.</summary>
    private void OnAutoScrollTick(object? sender, EventArgs e)
    {
        if (_autoScrollStep == 0 || _draggedRows is null || Vm is not { } vm)
        {
            StopAutoScroll();
            return;
        }

        var top = HeaderHeight();
        var visible = _grid.GetVisualDescendants().OfType<DataGridRow>()
            .Where(r => r.IsVisible && r.DataContext is TagFileItemViewModel)
            .Select(r => (Row: r, Y: r.TranslatePoint(default, _grid)?.Y))
            .Where(t => t.Y is { } y && y >= top - 1 && y + t.Row.Bounds.Height <= _grid.Bounds.Height + 1)
            .Select(t => vm.Files.IndexOf((TagFileItemViewModel)t.Row.DataContext!))
            .Where(i => i >= 0)
            .ToList();
        if (visible.Count == 0) return;

        var next = _autoScrollStep < 0 ? visible.Min() - 1 : visible.Max() + 1;
        if (next < 0 || next >= vm.Files.Count)
        {
            StopAutoScroll();
            return;
        }

        _grid.ScrollIntoView(vm.Files[next], null);
        _dropIndicator.IsVisible = false; // stale until the next drag-over places it again
    }

    // ===== Drag & drop: row reordering and dropping files in =====

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.DataTransfer.Contains(RowDragFormat))
        {
            if (_draggedRows is null || Vm is not { } vm)
            {
                e.DragEffects = DragDropEffects.None;
                return;
            }

            e.DragEffects = DragDropEffects.Move;
            UpdateAutoScroll(e);
            var (_, lineY) = DropTarget(e, vm);
            _dropIndicator.IsVisible = lineY is not null;
            if (lineY is { } top)
                _dropIndicator.Margin = new Thickness(0, Math.Max(0, top - 1), 0, 0);
            return;
        }

        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        _dropIndicator.IsVisible = false;
        StopAutoScroll();
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        _dropIndicator.IsVisible = false;
        StopAutoScroll();
        if (Vm is not { } vm)
            return;
        e.Handled = true;

        if (e.DataTransfer.Contains(RowDragFormat))
        {
            if (_draggedRows is not { } rows)
                return;

            vm.MoveFiles(rows, DropTarget(e, vm).Index);
            _grid.SelectedItems.Clear();
            foreach (var row in rows)
                _grid.SelectedItems.Add(row);
            _grid.ScrollIntoView(rows[0], null);
            e.DragEffects = DragDropEffects.Move;
            return;
        }

        var files = e.DataTransfer.TryGetFiles();
        if (files is null)
            return;

        var paths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .ToList();

        if (paths.Count > 0)
            vm.AddFiles(paths);
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MetadataEditorViewModel vm && sender is DataGrid grid)
            vm.SetSelection(grid.SelectedItems);
    }

    private async void OnAddFiles(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MetadataEditorViewModel vm)
            return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;

        var taggable = new FilePickerFileType($"{vm.FormatsText} files")
        {
            Patterns = vm.FilePatterns
        };

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add files",
            AllowMultiple = true,
            FileTypeFilter = new[] { taggable }
        });

        var paths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .ToList();

        if (paths.Count > 0)
            vm.AddFiles(paths);
    }

    private async void OnChooseCover(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MetadataEditorViewModel vm)
            return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose cover image",
            AllowMultiple = false,
            FileTypeFilter = new[] { FilePickerFileTypes.ImageAll }
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (!string.IsNullOrEmpty(path))
            vm.PickCover(path);
    }
}
