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

/// <summary>月历视图：按截止日期归组现有待办，点条目先查看详情，可再进入编辑。</summary>
public sealed partial class CalendarWindow : Window
{
    private const int DefaultLogicalWidth = 860;
    private const int DefaultLogicalHeight = 560;
    private const int MinLogicalWidth = 640;
    private const int MinLogicalHeight = 440;

    private readonly MainViewModel _viewModel;
    private readonly SettingsService _settings;
    private readonly Action<TodoItem> _editRequested;
    private readonly Action<TodoItem, DateTime> _rescheduleRequested;
    private readonly Action<DateTime> _createRequested;
    private const string DragKey = "ToDoooCalendarTask";
    private CalendarEntry? _draggedEntry;
    private bool _previewOpen;
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private DesktopAcrylicController? _acrylic;
    private SystemBackdropConfiguration? _backdropConfig;

    private DateTime _month;
    private DateTime _selected;
    private bool _closed;
    private double _lastLayoutWidth;
    private double _lastLayoutHeight;
    private int _resizeHit;
    private NativeMethods.POINT _resizeStartCursor;
    private NativeMethods.RECT _resizeStartRect;
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

        Root.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(Root_PointerPressed),
            handledEventsToo: true);
        Root.AddHandler(
            UIElement.PointerMovedEvent,
            new PointerEventHandler(Root_PointerMoved),
            handledEventsToo: true);
        Root.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(Root_PointerReleased),
            handledEventsToo: true);

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
        var width = Math.Max(MinLogicalWidth, _settings.Current.CalendarWidth);
        var height = Math.Max(MinLogicalHeight, _settings.Current.CalendarHeight);
        _appWindow.Resize(new SizeInt32(
            (int)Math.Round(width * scale),
            (int)Math.Round(height * scale)));
    }

    private double GetUiScale()
    {
        var width = Root.ActualWidth > 0 ? Root.ActualWidth : Math.Max(MinLogicalWidth, _settings.Current.CalendarWidth);
        var height = Root.ActualHeight > 0 ? Root.ActualHeight : Math.Max(MinLogicalHeight, _settings.Current.CalendarHeight);
        return Math.Clamp(Math.Min(width / DefaultLogicalWidth, height / DefaultLogicalHeight), 0.8, 1.6);
    }

    private void ApplyResponsiveMetrics(double scale)
    {
        var totalWidth = Root.ActualWidth > 0 ? Root.ActualWidth : Math.Max(MinLogicalWidth, _settings.Current.CalendarWidth);
        RightPanelColumn.Width = new GridLength(Math.Round(Math.Clamp(totalWidth * 0.30, 210, 420)));
        HeaderTitleText.FontSize = Math.Round(14 * scale, 1);
        MonthTitle.FontSize = Math.Round(12 * scale, 1);
        MonthTitle.Width = Math.Round(96 * scale);
        SelectedDateText.FontSize = Math.Round(13 * scale, 1);
        UndatedHeaderText.FontSize = Math.Round(11 * scale, 1);
        UndatedCountText.FontSize = Math.Round(11 * scale, 1);

        foreach (var child in WeekdayRow.Children)
        {
            if (child is TextBlock label)
            {
                label.FontSize = Math.Round(10 * scale, 1);
            }
        }
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

        ApplyResponsiveMetrics(GetUiScale());
        MonthTitle.Text = $"{_month:yyyy 年 M 月}";

        var entries = BuildEntries();
        var dated = entries
            .Where(entry => entry.DueAt is not null)
            .GroupBy(entry => entry.DueAt!.Value.Date)
            .ToDictionary(group => group.Key, group => group
                .OrderBy(entry => entry.DueAt)
                .ThenBy(entry => entry.Title)
                .ToList());

        BuildMonthCells(dated);
        ShowSelectedDay(dated.TryGetValue(_selected, out var dayEntries) ? dayEntries : new List<CalendarEntry>());

        var undated = _viewModel.AllItems
            .Where(item => item.DueAt is null && !item.IsDone)
            .Select(item => new CalendarEntry(item, null))
            .ToList();
        ShowUndated(undated);
    }

    private List<CalendarEntry> BuildEntries()
    {
        var entries = new List<CalendarEntry>();
        foreach (var item in _viewModel.AllItems)
        {
            if (item.DueAt is not null)
            {
                entries.Add(new CalendarEntry(item, null));
            }

            foreach (var subtask in item.Subtasks)
            {
                if (subtask.DueAt is not null)
                {
                    entries.Add(new CalendarEntry(item, subtask));
                }
            }
        }

        return entries;
    }

    private sealed record CalendarEntry(TodoItem Parent, SubtaskItem? Subtask)
    {
        public DateTime? DueAt => Subtask is { } subtask ? subtask.DueAt : Parent.DueAt;

        public bool IsDone => Subtask is { } subtask ? subtask.IsDone : Parent.IsDone;

        public bool IsOverdue => !IsDone && DueAt is { } due && due < DateTime.Now;

        public int TagIndex => Parent.TagIndex;

        public string Title => Subtask?.Title ?? Parent.Title;

        public string FullTitle => Subtask is null ? Parent.Title : $"{Parent.Title} › {Subtask.Title}";
    }

    private void BuildMonthCells(Dictionary<DateTime, List<CalendarEntry>> dated)
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

    private Border BuildCell(DateTime date, List<CalendarEntry>? entries)
    {
        var scale = GetUiScale();
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
            FontSize = Math.Round(11 * scale, 1),
            Margin = new Thickness(2, 0, 0, 2),
            Foreground = (Brush)Application.Current.Resources[
                isToday ? "AccentBrush" : inMonth ? "TextPrimaryBrush" : "TextMutedBrush"],
            FontWeight = isToday ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Opacity = inMonth ? 1 : 0.55,
        };
        content.Children.Add(dayNumber);

        if (entries is { Count: > 0 })
        {
            var gridHeight = DaysGrid.ActualHeight > 0
                ? DaysGrid.ActualHeight
                : Math.Max(MinLogicalHeight, _settings.Current.CalendarHeight) - 84;
            var cellHeight = gridHeight / 6.0;
            var maxChips = Math.Clamp((int)Math.Floor((cellHeight - 20 * scale) / (14 * scale)), 1, 6);

            var chips = new StackPanel
            {
                Spacing = 1,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            Grid.SetRow(chips, 1);

            foreach (var entry in entries.Take(maxChips))
            {
                chips.Children.Add(BuildChip(entry, scale));
            }

            if (entries.Count > maxChips)
            {
                chips.Children.Add(new TextBlock
                {
                    Text = $"+{entries.Count - maxChips}",
                    FontSize = Math.Round(9 * scale, 1),
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

        cell.Drop += async (sender, args) =>
        {
            cell.Background = GetCellBackground(date == _selected);
            if (_draggedEntry is not { } entry || sender is not FrameworkElement { Tag: DateTime target })
            {
                return;
            }

            args.Handled = true;
            _draggedEntry = null;

            if (entry.Subtask is { } subtask)
            {
                if (subtask.DueAt is null)
                {
                    var dueAt = await PromptForTimeAsync(target);
                    if (dueAt is { } value)
                    {
                        _viewModel.SetSubtaskDue(entry.Parent, subtask, value);
                        Refresh();
                    }

                    return;
                }

                var next = target.Date + subtask.DueAt.Value.TimeOfDay;
                _viewModel.SetSubtaskDue(entry.Parent, subtask, next);
                Refresh();
                return;
            }

            if (entry.Parent.DueAt is null)
            {
                var dueAt = await PromptForTimeAsync(target);
                if (dueAt is { } value)
                {
                    _viewModel.ScheduleAt(entry.Parent, value);
                    Refresh();
                }

                return;
            }

            _rescheduleRequested(entry.Parent, target);
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
        var entries = BuildEntries()
            .Where(entry => entry.DueAt is not null && entry.DueAt.Value.Date == _selected)
            .OrderBy(entry => entry.DueAt)
            .ThenBy(entry => entry.Title)
            .ToList();
        ShowSelectedDay(entries);
    }

    private static Brush GetCellBackground(bool selected) =>
        selected
            ? (Brush)Application.Current.Resources["SegmentSelectedBrush"]
            : (Brush)Application.Current.Resources["TransparentBrush"];

    private UIElement BuildChip(CalendarEntry entry, double scale)
    {
        var chip = new Grid
        {
            ColumnSpacing = Math.Round(3 * scale),
            Opacity = entry.IsDone ? 0.45 : 1,
            Background = (Brush)Application.Current.Resources["TransparentBrush"],
            CanDrag = true,
        };
        chip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        chip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        chip.DragStarting += (_, args) =>
        {
            _draggedEntry = entry;
            args.Data.Properties[DragKey] = true;
            args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        };
        chip.DropCompleted += (_, _) => _draggedEntry = null;
        chip.Tapped += async (_, args) =>
        {
            args.Handled = true;
            await ShowItemPreviewAsync(entry);
        };

        if (entry.Subtask is not null)
        {
            chip.Children.Add(new TextBlock
            {
                Text = "\u21B3",
                FontSize = Math.Round(9 * scale, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(TagColor(entry.TagIndex)),
            });
        }
        else
        {
            var dotSize = Math.Max(4, Math.Round(5 * scale));
            chip.Children.Add(new Border
            {
                Width = dotSize,
                Height = dotSize,
                CornerRadius = new CornerRadius(dotSize / 2),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(TagColor(entry.TagIndex)),
            });
        }

        var title = new TextBlock
        {
            Text = entry.Title,
            FontSize = Math.Round(9.5 * scale, 1),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextDecorations = entry.IsDone ? Windows.UI.Text.TextDecorations.Strikethrough : Windows.UI.Text.TextDecorations.None,
            Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
        };
        SetTitleToolTip(title, entry.FullTitle);
        Grid.SetColumn(title, 1);
        chip.Children.Add(title);

        return chip;
    }

    private static void SetTitleToolTip(DependencyObject target, string title) =>
        ToolTipService.SetToolTip(target, new ToolTip
        {
            Content = new TextBlock
            {
                Text = title,
                MaxWidth = 280,
                TextWrapping = TextWrapping.Wrap,
            },
        });

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

    private void ShowSelectedDay(List<CalendarEntry> entries)
    {
        SelectedDateText.Text = _selected.ToString("M 月 d 日 dddd");
        DetailsPanel.Children.Clear();

        if (entries.Count == 0)
        {
            DetailsPanel.Children.Add(new TextBlock
            {
                Text = "这天没有待办",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
            });
            return;
        }

        var parentsDueToday = entries
            .Where(entry => entry.Subtask is null)
            .Select(entry => entry.Parent.Id)
            .ToHashSet();

        foreach (var entry in entries)
        {
            if (entry.Subtask is not null && parentsDueToday.Contains(entry.Parent.Id))
            {
                continue;
            }

            DetailsPanel.Children.Add(BuildDetailRow(entry));

            if (entry.Subtask is null)
            {
                AddSubtaskRows(DetailsPanel, entry.Parent, _selected, undatedOnly: false);
            }
        }
    }

    private void ShowUndated(List<CalendarEntry> entries)
    {
        UndatedCountText.Text = entries.Count > 0 ? $"{entries.Count} 条" : string.Empty;
        UndatedPanel.Children.Clear();

        if (entries.Count == 0)
        {
            UndatedPanel.Children.Add(new TextBlock
            {
                Text = "没有未安排日期的待办",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
            });
            return;
        }

        foreach (var entry in entries)
        {
            UndatedPanel.Children.Add(BuildDetailRow(entry));
            AddSubtaskRows(UndatedPanel, entry.Parent, null, undatedOnly: true, showUndated: false);
        }
    }

    private async Task<DateTime?> PromptForTimeAsync(DateTime date)
    {
        var timePicker = new TimePicker
        {
            ClockIdentifier = "24HourClock",
            MinuteIncrement = 5,
            SelectedTime = RoundToFiveMinutes(DateTime.Now.TimeOfDay),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = $"{date:M 月 d 日 dddd}",
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
        });
        panel.Children.Add(timePicker);

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "设置时间",
            Content = panel,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.RequestedTheme = Root.RequestedTheme == ElementTheme.Default
            ? Appearance.ToElementTheme((ThemeMode)_settings.Current.ThemeMode)
            : Root.RequestedTheme;
        ThemePalette.ApplyTextControlPalette(dialog.Resources);
        dialog.Resources["ContentDialogBackground"] = Application.Current.Resources["DialogAcrylicBrush"];
        dialog.Resources["ContentDialogBorderBrush"] = Application.Current.Resources["CardStrokeBrush"];
        dialog.Resources["ContentDialogCornerRadius"] = new CornerRadius(8);
        dialog.Resources["ContentDialogSmokeFill"] = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x00, 0x00));

        if (await dialog.ShowAsync() != ContentDialogResult.Primary ||
            timePicker.SelectedTime is not { } time)
        {
            return null;
        }

        return date.Date + time;
    }

    private static TimeSpan RoundToFiveMinutes(TimeSpan value)
    {
        var minutes = (int)Math.Round(value.TotalMinutes / 5.0) * 5;
        return TimeSpan.FromMinutes(minutes % (24 * 60));
    }

    private Grid BuildDetailRow(CalendarEntry entry)
    {
        var scale = GetUiScale();
        var row = new Grid
        {
            Padding = new Thickness(6, Math.Round(5 * scale), 6, Math.Round(5 * scale)),
            Background = (Brush)Application.Current.Resources["TransparentBrush"],
            CanDrag = true,
        };
        row.Tapped += async (_, _) => await ShowItemPreviewAsync(entry);
        row.PointerEntered += (_, _) =>
            row.Background = (Brush)Application.Current.Resources["HoverBrush"];
        row.PointerExited += (_, _) =>
            row.Background = (Brush)Application.Current.Resources["TransparentBrush"];
        row.DragStarting += (sender, args) =>
        {
            _draggedEntry = entry;
            args.Data.Properties[DragKey] = true;
            args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        };
        row.DropCompleted += (_, _) => _draggedEntry = null;

        var content = new Grid { ColumnSpacing = 6 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dotSize = Math.Max(5, Math.Round(7 * scale));
        var dot = new Border
        {
            Width = dotSize,
            Height = dotSize,
            CornerRadius = new CornerRadius(dotSize / 2),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(TagColor(entry.TagIndex)),
        };
        content.Children.Add(dot);

        var textStack = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var title = new TextBlock
        {
            Text = entry.Title,
            FontSize = Math.Round(12 * scale, 1),
            MaxLines = 2,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)Application.Current.Resources[
                entry.IsDone ? "TextMutedBrush" : "TextPrimaryBrush"],
            TextDecorations = entry.IsDone
                ? Windows.UI.Text.TextDecorations.Strikethrough
                : Windows.UI.Text.TextDecorations.None,
        };
        SetTitleToolTip(title, entry.FullTitle);
        textStack.Children.Add(title);

        if (entry.Subtask is not null)
        {
            var parentLine = new Grid { ColumnSpacing = 3 };
            parentLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            parentLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var marker = new TextBlock
            {
                Text = "\u21B3",
                FontSize = Math.Round(9 * scale, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
            };
            parentLine.Children.Add(marker);

            var parentTitle = new TextBlock
            {
                Text = entry.Parent.Title,
                FontSize = Math.Round(10 * scale, 1),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
            };
            SetTitleToolTip(parentTitle, entry.Parent.Title);
            Grid.SetColumn(parentTitle, 1);
            parentLine.Children.Add(parentTitle);

            textStack.Children.Add(parentLine);
        }

        Grid.SetColumn(textStack, 1);
        content.Children.Add(textStack);

        var time = new TextBlock
        {
            Text = entry.DueAt?.ToString("HH:mm") ?? string.Empty,
            FontSize = Math.Round(11 * scale, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources[
                entry.IsOverdue ? "OverdueBrush" : "TextMutedBrush"],
        };
        Grid.SetColumn(time, 2);
        content.Children.Add(time);

        row.Children.Add(content);
        return row;
    }

    private void AddSubtaskRows(
        StackPanel panel,
        TodoItem parent,
        DateTime? selectedDate,
        bool undatedOnly,
        bool showUndated = true)
    {
        foreach (var subtask in parent.Subtasks)
        {
            if (undatedOnly && (subtask.DueAt is not null || subtask.IsDone))
            {
                continue;
            }

            panel.Children.Add(BuildSubtaskRow(parent, subtask, selectedDate, showUndated));
        }
    }

    private Grid BuildSubtaskRow(
        TodoItem parent,
        SubtaskItem subtask,
        DateTime? selectedDate,
        bool showUndated = true)
    {
        var scale = GetUiScale();
        var entry = new CalendarEntry(parent, subtask);
        var row = new Grid
        {
            Padding = new Thickness(Math.Round(18 * scale), 3, 6, 3),
            Background = (Brush)Application.Current.Resources["TransparentBrush"],
            CanDrag = true,
        };
        row.Tapped += async (_, _) => await ShowItemPreviewAsync(entry);
        row.PointerEntered += (_, _) =>
            row.Background = (Brush)Application.Current.Resources["HoverBrush"];
        row.PointerExited += (_, _) =>
            row.Background = (Brush)Application.Current.Resources["TransparentBrush"];
        row.DragStarting += (sender, args) =>
        {
            _draggedEntry = entry;
            args.Data.Properties[DragKey] = true;
            args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        };
        row.DropCompleted += (_, _) => _draggedEntry = null;

        var content = new Grid { ColumnSpacing = 6 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        content.Children.Add(new TextBlock
        {
            Text = "\u21B3",
            FontSize = Math.Round(9 * scale, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(TagColor(parent.TagIndex)),
        });

        var title = new TextBlock
        {
            Text = subtask.Title,
            FontSize = Math.Round(11 * scale, 1),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources[
                subtask.IsDone ? "TextMutedBrush" : "TextPrimaryBrush"],
            TextDecorations = subtask.IsDone
                ? Windows.UI.Text.TextDecorations.Strikethrough
                : Windows.UI.Text.TextDecorations.None,
        };
        SetTitleToolTip(title, entry.FullTitle);
        Grid.SetColumn(title, 1);
        content.Children.Add(title);

        var due = subtask.DueAt;
        var otherDay = due is { } dueValue && selectedDate is { } day && dueValue.Date != day.Date;
        var timeText = due is null
            ? showUndated ? "未定" : string.Empty
            : otherDay
                ? $"→ {due.Value:M/d HH:mm}"
                : selectedDate is null
                    ? due.Value.ToString("M/d HH:mm")
                    : due.Value.ToString("HH:mm");
        if (timeText.Length > 0)
        {
            var time = new TextBlock
            {
                Text = timeText,
                FontSize = Math.Round(10 * scale, 1),
                VerticalAlignment = VerticalAlignment.Center,
                FontStyle = otherDay
                    ? Windows.UI.Text.FontStyle.Italic
                    : Windows.UI.Text.FontStyle.Normal,
                Foreground = (Brush)Application.Current.Resources[
                    entry.IsOverdue ? "OverdueBrush" : "TextMutedBrush"],
            };
            Grid.SetColumn(time, 2);
            content.Children.Add(time);
        }

        row.Children.Add(content);
        return row;
    }

    private async Task ShowItemPreviewAsync(CalendarEntry entry)
    {
        if (_previewOpen || _closed)
        {
            return;
        }

        _previewOpen = true;
        try
        {
            var panel = new StackPanel { Spacing = 12 };

            var header = new Grid { ColumnSpacing = 8 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.Children.Add(new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(TagColor(entry.TagIndex)),
            });
            var title = new TextBlock
            {
                Text = entry.Title,
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            };
            Grid.SetColumn(title, 1);
            header.Children.Add(title);
            panel.Children.Add(header);

            if (entry.Subtask is not null)
            {
                AddPreviewRow(panel, "主任务", entry.Parent.Title);
            }

            var due = entry.DueAt;
            AddPreviewRow(
                panel,
                "时间",
                due is { } dueValue ? dueValue.ToString("yyyy/M/d HH:mm") : "无截止时间",
                due is not null && entry.IsOverdue
                    ? (Brush)Application.Current.Resources["OverdueBrush"]
                    : null);
            AddPreviewRow(
                panel,
                "状态",
                entry.IsDone ? "已完成" : entry.IsOverdue ? "已逾期" : "未完成",
                entry.IsOverdue
                    ? (Brush)Application.Current.Resources["OverdueBrush"]
                    : null);

            if (entry.Subtask is null)
            {
                var parent = entry.Parent;
                if (parent.Recurrence != TodoRecurrence.None)
                {
                    AddPreviewRow(panel, "重复", parent.RecurrenceText);
                }

                if (parent.IsImportant)
                {
                    AddPreviewRow(panel, "标记", "重要");
                }

                if (parent.Subtasks.Count > 0)
                {
                    AddPreviewRow(panel, "子任务", parent.SubtaskProgressText);
                    var list = new StackPanel { Spacing = 6, Margin = new Thickness(0, 2, 0, 0) };
                    foreach (var subtask in parent.Subtasks)
                    {
                        list.Children.Add(BuildSubtaskPreviewRow(subtask));
                    }

                    panel.Children.Add(list);
                }
            }

            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = entry.Subtask is null ? "任务详情" : "子任务详情",
                Content = new ScrollViewer
                {
                    Content = panel,
                    MaxHeight = 380,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                },
                PrimaryButtonText = "编辑",
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close,
            };
            dialog.RequestedTheme = Root.RequestedTheme == ElementTheme.Default
                ? Appearance.ToElementTheme((ThemeMode)_settings.Current.ThemeMode)
                : Root.RequestedTheme;
            ThemePalette.ApplyTextControlPalette(dialog.Resources);
            dialog.Resources["ContentDialogBackground"] = Application.Current.Resources["DialogAcrylicBrush"];
            dialog.Resources["ContentDialogBorderBrush"] = Application.Current.Resources["CardStrokeBrush"];
            dialog.Resources["ContentDialogCornerRadius"] = new CornerRadius(8);
            dialog.Resources["ContentDialogSmokeFill"] = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x00, 0x00));

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                // 等对话框完全收掉再开编辑窗口，避免和这次点击的收尾抢输入队列。
                DispatcherQueue.TryEnqueue(() => _editRequested(entry.Parent));
            }
        }
        finally
        {
            _previewOpen = false;
        }
    }

    private static void AddPreviewRow(StackPanel panel, string label, string value, Brush? valueBrush = null)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
        });
        var valueText = new TextBlock
        {
            Text = value,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = valueBrush ?? (Brush)Application.Current.Resources["TextPrimaryBrush"],
        };
        Grid.SetColumn(valueText, 1);
        row.Children.Add(valueText);
        panel.Children.Add(row);
    }

    private static Grid BuildSubtaskPreviewRow(SubtaskItem subtask)
    {
        var primary = (Brush)Application.Current.Resources["TextPrimaryBrush"];
        var secondary = (Brush)Application.Current.Resources["TextSecondaryBrush"];
        var muted = (Brush)Application.Current.Resources["TextMutedBrush"];
        var overdue = (Brush)Application.Current.Resources["OverdueBrush"];

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new TextBlock
        {
            Text = subtask.IsDone ? "✓" : "○",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = subtask.IsDone ? muted : secondary,
        });

        var title = new TextBlock
        {
            Text = subtask.Title,
            FontSize = 11,
            MaxLines = 2,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = subtask.IsDone ? muted : primary,
            TextDecorations = subtask.IsDone
                ? Windows.UI.Text.TextDecorations.Strikethrough
                : Windows.UI.Text.TextDecorations.None,
        };
        SetTitleToolTip(title, subtask.Title);
        Grid.SetColumn(title, 1);
        row.Children.Add(title);

        var time = new TextBlock
        {
            Text = subtask.DueAt is { } due ? due.ToString("M/d HH:mm") : "无截止",
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = subtask.IsOverdue ? overdue : muted,
        };
        Grid.SetColumn(time, 2);
        row.Children.Add(time);

        return row;
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

    private void NewTask_Click(object sender, RoutedEventArgs e) => _createRequested(_selected);

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
        _draggedEntry = null;
    }

    private void UndatedDropZone_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Properties.ContainsKey(DragKey))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
            return;
        }

        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        e.DragUIOverride.IsCaptionVisible = false;
        e.DragUIOverride.IsGlyphVisible = false;
        UndatedDropZone.Background = (Brush)Application.Current.Resources["HoverBrush"];
    }

    private void UndatedDropZone_DragLeave(object sender, DragEventArgs e) =>
        UndatedDropZone.Background = (Brush)Application.Current.Resources["TransparentBrush"];

    private void UndatedDropZone_Drop(object sender, DragEventArgs e)
    {
        UndatedDropZone.Background = (Brush)Application.Current.Resources["TransparentBrush"];

        if (!e.DataView.Properties.ContainsKey(DragKey) || _draggedEntry is not { } entry)
        {
            return;
        }

        e.Handled = true;
        _draggedEntry = null;

        if (entry.Subtask is { } subtask)
        {
            if (subtask.DueAt is not null)
            {
                _viewModel.SetSubtaskDue(entry.Parent, subtask, null);
                Refresh();
            }

            return;
        }

        if (entry.Parent.DueAt is null)
        {
            return;
        }

        _viewModel.ClearDue(entry.Parent);
        Refresh();
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
            EdgeHit(e.GetCurrentPoint(Root).Position) != 0 ||
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

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_closed)
        {
            return;
        }

        if (Math.Abs(e.NewSize.Width - _lastLayoutWidth) < 4 &&
            Math.Abs(e.NewSize.Height - _lastLayoutHeight) < 4)
        {
            return;
        }

        _lastLayoutWidth = e.NewSize.Width;
        _lastLayoutHeight = e.NewSize.Height;
        Refresh();
    }

    private void Root_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        UpdateResizeCursor(e.GetCurrentPoint(Root).Position);

    private void Root_PointerExited(object sender, PointerRoutedEventArgs e) =>
        Root.SetCursor(null);

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var local = e.GetCurrentPoint(Root).Position;
        var hit = EdgeHit(local);
        if (hit == 0 ||
            !NativeMethods.GetCursorPos(out _resizeStartCursor) ||
            !NativeMethods.GetWindowRect(_hwnd, out _resizeStartRect))
        {
            return;
        }

        _resizeHit = hit;
        Root.CapturePointer(e.Pointer);
        UpdateResizeCursor(local);
        e.Handled = true;
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var local = e.GetCurrentPoint(Root).Position;
        if (_resizeHit == 0)
        {
            UpdateResizeCursor(local);
            return;
        }

        if (NativeMethods.GetCursorPos(out var cursor))
        {
            var scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
            var minWidth = (int)Math.Round(MinLogicalWidth * scale);
            var minHeight = (int)Math.Round(MinLogicalHeight * scale);
            var dx = cursor.X - _resizeStartCursor.X;
            var dy = cursor.Y - _resizeStartCursor.Y;

            var left = _resizeStartRect.Left;
            var top = _resizeStartRect.Top;
            var right = _resizeStartRect.Right;
            var bottom = _resizeStartRect.Bottom;

            if (_resizeHit is NativeMethods.HTLEFT or NativeMethods.HTTOPLEFT or NativeMethods.HTBOTTOMLEFT)
            {
                left = Math.Min(_resizeStartRect.Left + dx, _resizeStartRect.Right - minWidth);
            }

            if (_resizeHit is NativeMethods.HTRIGHT or NativeMethods.HTTOPRIGHT or NativeMethods.HTBOTTOMRIGHT)
            {
                right = Math.Max(_resizeStartRect.Right + dx, _resizeStartRect.Left + minWidth);
            }

            if (_resizeHit is NativeMethods.HTTOP or NativeMethods.HTTOPLEFT or NativeMethods.HTTOPRIGHT)
            {
                top = Math.Min(_resizeStartRect.Top + dy, _resizeStartRect.Bottom - minHeight);
            }

            if (_resizeHit is NativeMethods.HTBOTTOM or NativeMethods.HTBOTTOMLEFT or NativeMethods.HTBOTTOMRIGHT)
            {
                bottom = Math.Max(_resizeStartRect.Bottom + dy, _resizeStartRect.Top + minHeight);
            }

            NativeMethods.SetWindowPos(
                _hwnd, IntPtr.Zero, left, top, right - left, bottom - top,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }

        e.Handled = true;
    }

    private void Root_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_resizeHit == 0)
        {
            return;
        }

        _resizeHit = 0;
        Root.ReleasePointerCapture(e.Pointer);
        SaveCalendarSize();
        UpdateResizeCursor(e.GetCurrentPoint(Root).Position);
        e.Handled = true;
    }

    private void Root_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_resizeHit == 0)
        {
            return;
        }

        _resizeHit = 0;
        SaveCalendarSize();
        Root.SetCursor(null);
    }

    private void SaveCalendarSize()
    {
        if (_closed || !NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            return;
        }

        var scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        _settings.Current.CalendarWidth = Math.Max(MinLogicalWidth, (int)Math.Round((rect.Right - rect.Left) / scale));
        _settings.Current.CalendarHeight = Math.Max(MinLogicalHeight, (int)Math.Round((rect.Bottom - rect.Top) / scale));
        _settings.Save();
    }

    private int EdgeHit(Windows.Foundation.Point point)
    {
        const double edge = 8;
        var left = point.X < edge;
        var right = point.X > Root.ActualWidth - edge;
        var top = point.Y < edge;
        var bottom = point.Y > Root.ActualHeight - edge;

        return (left, right, top, bottom) switch
        {
            (true, _, true, _) => NativeMethods.HTTOPLEFT,
            (_, true, true, _) => NativeMethods.HTTOPRIGHT,
            (true, _, _, true) => NativeMethods.HTBOTTOMLEFT,
            (_, true, _, true) => NativeMethods.HTBOTTOMRIGHT,
            (true, _, _, _) => NativeMethods.HTLEFT,
            (_, true, _, _) => NativeMethods.HTRIGHT,
            (_, _, true, _) => NativeMethods.HTTOP,
            (_, _, _, true) => NativeMethods.HTBOTTOM,
            _ => 0,
        };
    }

    private void UpdateResizeCursor(Windows.Foundation.Point point)
    {
        var hit = _resizeHit != 0 ? _resizeHit : EdgeHit(point);
        Root.SetCursor(hit switch
        {
            NativeMethods.HTLEFT or NativeMethods.HTRIGHT => Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast,
            NativeMethods.HTTOP or NativeMethods.HTBOTTOM => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth,
            NativeMethods.HTTOPLEFT or NativeMethods.HTBOTTOMRIGHT => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthwestSoutheast,
            NativeMethods.HTTOPRIGHT or NativeMethods.HTBOTTOMLEFT => Microsoft.UI.Input.InputSystemCursorShape.SizeNortheastSouthwest,
            _ => null,
        });
    }
}