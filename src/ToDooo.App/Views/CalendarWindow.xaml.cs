using System.Collections.Specialized;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ToDooo.Helpers;
using ToDooo.Interop;
using ToDooo.Models;
using ToDooo.Services;
using ToDooo.ViewModels;
using Windows.Graphics;
using Windows.UI;

namespace ToDooo.Views;

/// <summary>月历视图：按截止日期归组现有待办，点条目直接打开编辑窗口。</summary>
public sealed partial class CalendarWindow : Window
{
    private const int LogicalWidth = 860;
    private const int LogicalHeight = 560;

    private readonly MainViewModel _viewModel;
    private readonly SettingsService _settings;
    private readonly Action<TodoItem> _editRequested;
    private readonly Action<TodoItem, DateTime> _rescheduleRequested;
    private readonly Action<DateTime> _createRequested;
    private const string DragKey = "ToDoooCalendarTask";
    private TodoItem? _draggedItem;
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private DesktopAcrylicController? _acrylic;
    private SystemBackdropConfiguration? _backdropConfig;

    private DateTime _month;
    private DateTime _selected;
    private bool _closed;
    private readonly List<(DateTime Date, Border Cell)> _cells = new();

    public CalendarWindow(
        MainViewModel viewModel,
        SettingsService settings,
        Action<TodoItem> editRequested,
        Action<TodoItem, DateTime> rescheduleRequested,
        Action<DateTime> createRequested)
    {
        _viewModel = viewModel;
        _settings = settings;
        _editRequested = editRequested;
        _rescheduleRequested = rescheduleRequested;
        _createRequested = createRequested;

        InitializeComponent();
        Root.RequestedTheme = Appearance.ToElementTheme((ThemeMode)settings.Current.ThemeMode);
        ThemePalette.ApplyTextControlPalette(Root.Resources);

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        ConfigureWindow();

        var today = DateTime.Today;
        _month = new DateTime(today.Year, today.Month, 1);
        _selected = today;

        BuildWeekdayHeader();
        Refresh();

        Activated += (_, _) => Refresh();
        _viewModel.Items.CollectionChanged += OnItemsChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            _viewModel.Items.CollectionChanged -= OnItemsChanged;
            _acrylic = null;
        };
    }

    public void ShowOver(AppWindow? owner)
    {
        // 先摆好位置和尺寸再显示：中途 Show 会让窗口按默认尺寸出现在旧位置，看着闪一下。
        MoveNextTo(owner);
        ApplyLogicalSize(DpiScale.ForOwner(owner, _hwnd));
        MoveNextTo(owner);
        _appWindow.Show();
        Activate();
    }

    private void ApplyLogicalSize(double scale)
    {
        _appWindow.Resize(new SizeInt32(
            (int)Math.Round(LogicalWidth * scale),
            (int)Math.Round(LogicalHeight * scale)));
    }

    public void RefreshTheme()
    {
        Root.RequestedTheme = Appearance.ToElementTheme((ThemeMode)_settings.Current.ThemeMode);
        ApplyWindowMaterial();
        Refresh();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_closed)
        {
            Refresh();
        }
    }

    private void ConfigureWindow()
    {
        _appWindow.Title = "日历";
        _appWindow.IsShownInSwitchers = false;

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        var style = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_STYLE).ToInt64();
        style &= ~(NativeMethods.WS_THICKFRAME | NativeMethods.WS_DLGFRAME
            | NativeMethods.WS_CAPTION | NativeMethods.WS_BORDER);
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_STYLE, new IntPtr(style));

        var round = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        var noBorder = NativeMethods.DWMWA_COLOR_NONE;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));

        ExtendsContentIntoTitleBar = false;
        HeaderBar.Padding = new Thickness(16, 0, 10, 0);
        ApplyWindowMaterial();
    }

    private void ApplyWindowMaterial()
    {
        var dark = Appearance.IsDark((ThemeMode)_settings.Current.ThemeMode);
        var darkFlag = dark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkFlag, sizeof(int));

        if (_acrylic is null)
        {
            _backdropConfig = new SystemBackdropConfiguration
            {
                IsInputActive = true,
                Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light,
            };
            _acrylic = WindowAcrylic.Apply(
                this, Appearance.TintForOpacity(_settings.Current.WindowOpacity), dark, _backdropConfig);
            return;
        }

        WindowAcrylic.ApplyTint(_acrylic, Appearance.TintForOpacity(_settings.Current.WindowOpacity), dark);
        if (_backdropConfig is not null)
        {
            _backdropConfig.Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
        }
    }

    private void MoveNextTo(AppWindow? owner)
    {
        var size = _appWindow.Size;
        var point = new PointInt32(0, 0);
        var placed = false;

        if (owner is not null)
        {
            var ownerHwnd = Win32Interop.GetWindowFromWindowId(owner.Id);
            if (NativeMethods.GetWindowRect(ownerHwnd, out var rect))
            {
                point = new PointInt32(
                    rect.Left + ((rect.Right - rect.Left) - size.Width) / 2,
                    rect.Top + ((rect.Bottom - rect.Top) - size.Height) / 2);
                placed = true;
            }
        }

        var area = DisplayArea.GetFromPoint(point, DisplayAreaFallback.Nearest).WorkArea;
        if (!placed)
        {
            point = new PointInt32(
                area.X + ((area.Width - size.Width) / 2),
                area.Y + ((area.Height - size.Height) / 2));
        }

        _appWindow.Move(new PointInt32(
            Math.Clamp(point.X, area.X, area.X + Math.Max(0, area.Width - size.Width)),
            Math.Clamp(point.Y, area.Y, area.Y + Math.Max(0, area.Height - size.Height))));
    }

    private void BuildWeekdayHeader()
    {
        WeekdayRow.Children.Clear();
        WeekdayRow.ColumnDefinitions.Clear();

        var labels = new[] { "一", "二", "三", "四", "五", "六", "日" };
        for (var i = 0; i < labels.Length; i++)
        {
            WeekdayRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var text = new TextBlock
            {
                Text = labels[i],
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
            };
            Grid.SetColumn(text, i);
            WeekdayRow.Children.Add(text);
        }
    }

    private void Refresh()
    {
        if (_closed)
        {
            return;
        }

        MonthTitle.Text = $"{_month:yyyy 年 M 月}";

        var dated = _viewModel.AllItems
            .Where(item => item.DueAt is not null)
            .GroupBy(item => item.DueAt!.Value.Date)
            .ToDictionary(group => group.Key, group => group
                .OrderBy(item => item.DueAt)
                .ThenBy(item => item.Title)
                .ToList());

        BuildMonthCells(dated);
        ShowSelectedDay(dated.TryGetValue(_selected, out var dayItems) ? dayItems : new List<TodoItem>());

        var undated = _viewModel.AllItems.Count(item => item.DueAt is null && !item.IsDone);
        UndatedText.Text = undated > 0 ? $"另有 {undated} 条无截止日期，未显示" : string.Empty;
    }

    private void BuildMonthCells(Dictionary<DateTime, List<TodoItem>> dated)
    {
        DaysGrid.Children.Clear();
        DaysGrid.RowDefinitions.Clear();
        DaysGrid.ColumnDefinitions.Clear();
        _cells.Clear();

        for (var row = 0; row < 6; row++)
        {
            DaysGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }

        for (var column = 0; column < 7; column++)
        {
            DaysGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var offset = ((int)_month.DayOfWeek + 6) % 7;
        var gridStart = _month.AddDays(-offset);

        for (var index = 0; index < 42; index++)
        {
            var date = gridStart.AddDays(index);
            var cell = BuildCell(date, dated.TryGetValue(date, out var items) ? items : null);
            Grid.SetRow(cell, index / 7);
            Grid.SetColumn(cell, index % 7);
            DaysGrid.Children.Add(cell);
            _cells.Add((date, cell));
        }
    }

    private Border BuildCell(DateTime date, List<TodoItem>? items)
    {
        var inMonth = date.Month == _month.Month;
        var isSelected = date == _selected;
        var isToday = date == DateTime.Today;

        var content = new Grid
        {
            Margin = new Thickness(2),
        };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var dayNumber = new TextBlock
        {
            Text = date.Day.ToString(),
            FontSize = 11,
            Margin = new Thickness(2, 0, 0, 2),
            Foreground = (Brush)Application.Current.Resources[
                isToday ? "AccentBrush" : inMonth ? "TextPrimaryBrush" : "TextMutedBrush"],
            FontWeight = isToday ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Opacity = inMonth ? 1 : 0.55,
        };
        content.Children.Add(dayNumber);

        if (items is { Count: > 0 })
        {
            var chips = new StackPanel
            {
                Spacing = 1,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            Grid.SetRow(chips, 1);

            foreach (var item in items.Take(3))
            {
                var chip = BuildChip(item);
                chip.IsHitTestVisible = false;
                chips.Children.Add(chip);
            }

            if (items.Count > 3)
            {
                chips.Children.Add(new TextBlock
                {
                    Text = $"+{items.Count - 3}",
                    FontSize = 9,
                    Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
                });
            }

            content.Children.Add(chips);
        }

        var cell = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(2),
            Background = GetCellBackground(isSelected),
            Child = content,
            AllowDrop = true,
        };

        cell.Tag = date;
        cell.Tapped += (sender, _) =>
        {
            if (sender is not FrameworkElement { Tag: DateTime tapped })
            {
                return;
            }

            _selected = tapped;
            if (tapped.Year != _month.Year || tapped.Month != _month.Month)
            {
                _month = new DateTime(tapped.Year, tapped.Month, 1);
                Refresh();
                return;
            }

            UpdateSelectionVisuals();
            ShowDetailsForSelected();
        };

        cell.DoubleTapped += (sender, args) =>
        {
            if (sender is FrameworkElement { Tag: DateTime tapped })
            {
                args.Handled = true;
                _selected = tapped;
                _month = new DateTime(tapped.Year, tapped.Month, 1);
                Refresh();
                _createRequested(tapped);
            }
        };

        cell.DragOver += (_, args) =>
        {
            args.AcceptedOperation = args.DataView.Properties.ContainsKey(DragKey)
                ? Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move
                : Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
            args.DragUIOverride.IsCaptionVisible = false;
            args.DragUIOverride.IsGlyphVisible = false;
            cell.Background = (Brush)Application.Current.Resources["HoverBrush"];
        };

        cell.DragLeave += (_, _) => cell.Background = GetCellBackground(date == _selected);

        cell.Drop += (sender, args) =>
        {
            cell.Background = GetCellBackground(date == _selected);
            if (_draggedItem is not { } item || sender is not FrameworkElement { Tag: DateTime target })
            {
                return;
            }

            args.Handled = true;
            _draggedItem = null;
            _rescheduleRequested(item, target);
            Refresh();
        };

        cell.PointerEntered += (_, _) =>
            cell.Background = (Brush)Application.Current.Resources["HoverBrush"];
        cell.PointerExited += (_, _) =>
            cell.Background = GetCellBackground(date == _selected);

        return cell;
    }

    private void UpdateSelectionVisuals()
    {
        foreach (var (date, cell) in _cells)
        {
            cell.Background = GetCellBackground(date == _selected);
        }
    }

    private void ShowDetailsForSelected()
    {
        var items = _viewModel.AllItems
            .Where(item => item.DueAt is not null && item.DueAt.Value.Date == _selected)
            .OrderBy(item => item.DueAt)
            .ThenBy(item => item.Title)
            .ToList();
        ShowSelectedDay(items);
    }

    private static Brush GetCellBackground(bool selected) =>
        selected
            ? (Brush)Application.Current.Resources["SegmentSelectedBrush"]
            : (Brush)Application.Current.Resources["TransparentBrush"];

    private UIElement BuildChip(TodoItem item)
    {
        var chip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3,
            Opacity = item.IsDone ? 0.45 : 1,
        };

        chip.Children.Add(new Border
        {
            Width = 5,
            Height = 5,
            CornerRadius = new CornerRadius(2.5),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(TagColor(item.TagIndex)),
        });

        chip.Children.Add(new TextBlock
        {
            Text = item.Title,
            FontSize = 9,
            MaxLines = 1,
            MaxWidth = 58,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextDecorations = item.IsDone ? Windows.UI.Text.TextDecorations.Strikethrough : Windows.UI.Text.TextDecorations.None,
            Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
        });

        return chip;
    }

    private static Color TagColor(int tagIndex)
    {
        if (tagIndex < 0 || tagIndex >= MainViewModel.TagColors.Length)
        {
            return Color.FromArgb(255, 0x8A, 0x8A, 0x92);
        }

        var hex = MainViewModel.TagColors[tagIndex];
        return Color.FromArgb(
            255,
            Convert.ToByte(hex.Substring(1, 2), 16),
            Convert.ToByte(hex.Substring(3, 2), 16),
            Convert.ToByte(hex.Substring(5, 2), 16));
    }

    private void ShowSelectedDay(List<TodoItem> items)
    {
        SelectedDateText.Text = _selected.ToString("M 月 d 日 dddd");
        DetailsPanel.Children.Clear();

        if (items.Count == 0)
        {
            DetailsPanel.Children.Add(new TextBlock
            {
                Text = "这天没有待办",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
            });
            return;
        }

        foreach (var item in items)
        {
            var row = new Grid
            {
                Padding = new Thickness(6, 5, 6, 5),
                Background = (Brush)Application.Current.Resources["TransparentBrush"],
                CanDrag = true,
            };
            row.Tapped += (_, _) => _editRequested(item);
            row.PointerEntered += (_, _) =>
                row.Background = (Brush)Application.Current.Resources["HoverBrush"];
            row.PointerExited += (_, _) =>
                row.Background = (Brush)Application.Current.Resources["TransparentBrush"];
            row.DragStarting += (sender, args) =>
            {
                _draggedItem = item;
                args.Data.Properties[DragKey] = true;
                args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
            };
            row.DropCompleted += (_, _) => _draggedItem = null;

            var content = new Grid { ColumnSpacing = 6 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dot = new Border
            {
                Width = 7,
                Height = 7,
                CornerRadius = new CornerRadius(3.5),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(TagColor(item.TagIndex)),
            };
            content.Children.Add(dot);

            var title = new TextBlock
            {
                Text = item.Title,
                FontSize = 12,
                MaxLines = 2,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources[
                    item.IsDone ? "TextMutedBrush" : "TextPrimaryBrush"],
                TextDecorations = item.IsDone
                    ? Windows.UI.Text.TextDecorations.Strikethrough
                    : Windows.UI.Text.TextDecorations.None,
            };
            Grid.SetColumn(title, 1);
            content.Children.Add(title);

            var time = new TextBlock
            {
                Text = item.DueAt?.ToString("HH:mm") ?? string.Empty,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources[
                    item.IsOverdue ? "OverdueBrush" : "TextMutedBrush"],
            };
            Grid.SetColumn(time, 2);
            content.Children.Add(time);

            row.Children.Add(content);
            DetailsPanel.Children.Add(row);
        }
    }

    private void PrevMonth_Click(object sender, RoutedEventArgs e)
    {
        _month = _month.AddMonths(-1);
        Refresh();
    }

    private void NextMonth_Click(object sender, RoutedEventArgs e)
    {
        _month = _month.AddMonths(1);
        Refresh();
    }

    private void Today_Click(object sender, RoutedEventArgs e)
    {
        _selected = DateTime.Today;
        _month = new DateTime(_selected.Year, _selected.Month, 1);
        Refresh();
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Properties.ContainsKey(DragKey))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
            e.DragUIOverride.IsCaptionVisible = false;
            e.DragUIOverride.IsGlyphVisible = false;
        }
    }

    private void Root_Drop(object sender, DragEventArgs e)
    {
        _draggedItem = null;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void CloseAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Close();
    }

    private bool _dragging;
    private NativeMethods.POINT _dragStartCursor;
    private NativeMethods.RECT _dragStartRect;

    private void HeaderBar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint((UIElement)sender).Properties.IsLeftButtonPressed ||
            !NativeMethods.GetCursorPos(out _dragStartCursor) ||
            !NativeMethods.GetWindowRect(_hwnd, out _dragStartRect))
        {
            return;
        }

        _dragging = ((UIElement)sender).CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void HeaderBar_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        if (NativeMethods.GetCursorPos(out var cursor))
        {
            NativeMethods.SetWindowPos(
                _hwnd, IntPtr.Zero,
                _dragStartRect.Left + cursor.X - _dragStartCursor.X,
                _dragStartRect.Top + cursor.Y - _dragStartCursor.Y,
                _dragStartRect.Right - _dragStartRect.Left,
                _dragStartRect.Bottom - _dragStartRect.Top,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }

        e.Handled = true;
    }

    private void HeaderBar_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void HeaderBar_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => _dragging = false;
}