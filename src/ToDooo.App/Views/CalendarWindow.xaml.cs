using System.Collections.Specialized;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ToDooo.Models;
using ToDooo.Services;
using ToDooo.ViewModels;
using Windows.UI;

namespace ToDooo.Views;

/// <summary>月历面板：嵌在主窗口左侧展开，按截止日期归组现有待办，点条目先查看详情，可再进入编辑。</summary>
public sealed partial class CalendarWindow : UserControl
{
    private const int DefaultLogicalWidth = 820;
    private const int DefaultDrawerLogicalWidth = 260;
    private const int DefaultLogicalHeight = 560;
    private const int MinLogicalWidth = 580;
    private const int MinCollapsedLogicalWidth = 400;
    private const int MinLogicalHeight = 440;

    private MainViewModel? _viewModel;
    private SettingsService? _settings;
    private Action<TodoItem>? _editRequested;
    private Action<TodoItem, DateTime>? _rescheduleRequested;
    private Action<DateTime>? _createRequested;
    private Action<Microsoft.UI.Input.InputSystemCursorShape?>? _setCursor;
    private const string DragKey = "ToDoooCalendarTask";
    private CalendarEntry? _draggedEntry;
    private bool _previewOpen;

    private DateTime _month;
    private DateTime _selected;
    private double _lastLayoutWidth;
    private double _lastLayoutHeight;
    private bool _selectedDayHasEntries;
    private bool _undatedHasEntries;
    private bool _rightPanelCollapsed;
    private double _savedDrawerLogicalWidth = DefaultDrawerLogicalWidth;
    private bool _customRightSplit;
    private double _rightSplitRatio = 0.5;
    private bool _splittingRightPanel;
    private bool _hoveringRightSplitter;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _dragMonthNavTimer;
    private int _dragMonthNavDelta;
    private readonly List<(DateTime Date, Border Cell)> _cells = new();

    public event EventHandler<PointerRoutedEventArgs>? HeaderDragPressed;
    public event EventHandler<PointerRoutedEventArgs>? HeaderDragMoved;
    public event EventHandler<PointerRoutedEventArgs>? HeaderDragReleased;
    public event EventHandler<PointerRoutedEventArgs>? HeaderDragCaptureLost;
    public event EventHandler<bool>? RightPanelCollapsedChanged;

    public bool IsRightPanelCollapsed => _rightPanelCollapsed;

    public double CurrentDrawerWidthLogical => _savedDrawerLogicalWidth;

    public CalendarWindow()
    {
        InitializeComponent();
        ElementCompositionPreview.SetIsTranslationEnabled(RightPanelGrid, true);
        var drawerHostVisual = ElementCompositionPreview.GetElementVisual(RightDrawerHost);
        drawerHostVisual.Clip = drawerHostVisual.Compositor.CreateInsetClip();

        var today = DateTime.Today;
        _month = new DateTime(today.Year, today.Month, 1);
        _selected = today;
        BuildWeekdayHeader();
    }

    public void Attach(
        MainViewModel viewModel,
        SettingsService settings,
        Action<TodoItem> editRequested,
        Action<TodoItem, DateTime> rescheduleRequested,
        Action<DateTime> createRequested,
        Action<Microsoft.UI.Input.InputSystemCursorShape?> setCursor)
    {
        if (_viewModel is not null)
        {
            _viewModel.Items.CollectionChanged -= OnItemsChanged;
        }

        _viewModel = viewModel;
        _settings = settings;
        _editRequested = editRequested;
        _rescheduleRequested = rescheduleRequested;
        _createRequested = createRequested;
        _setCursor = setCursor;

        _viewModel.Items.CollectionChanged += OnItemsChanged;
        RefreshTheme();
    }

    public void BeginExternalDrag(
        TodoItem parent,
        SubtaskItem? subtask,
        Windows.ApplicationModel.DataTransfer.DataPackage data)
    {
        _draggedEntry = new CalendarEntry(parent, subtask);
        data.Properties[DragKey] = true;
        data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
    }

    public void EndExternalDrag() => _draggedEntry = null;

    private double GetUiScale()
    {
        var baseWidth = _rightPanelCollapsed
            ? DefaultLogicalWidth - DefaultDrawerLogicalWidth
            : DefaultLogicalWidth;
        var fallbackWidth = _settings is not null
            ? Math.Max(
                _rightPanelCollapsed ? MinCollapsedLogicalWidth : MinLogicalWidth,
                _settings.Current.CalendarWidth - (_rightPanelCollapsed ? _savedDrawerLogicalWidth : 0))
            : baseWidth;
        var fallbackHeight = _settings is not null ? Math.Max(MinLogicalHeight, _settings.Current.CalendarHeight) : DefaultLogicalHeight;
        var width = Root.ActualWidth > 0 ? Root.ActualWidth : fallbackWidth;
        var height = Root.ActualHeight > 0 ? Root.ActualHeight : fallbackHeight;
        return Math.Clamp(Math.Min(width / baseWidth, height / DefaultLogicalHeight), 0.82, 1.55);
    }

    private void ApplyResponsiveMetrics(double scale)
    {
        var fallbackWidth = _settings is not null ? Math.Max(MinLogicalWidth, _settings.Current.CalendarWidth) : DefaultLogicalWidth;
        var totalWidth = Root.ActualWidth > 0 ? Root.ActualWidth : fallbackWidth;
        if (!_rightPanelCollapsed)
        {
            var drawerCol = Math.Round(Math.Clamp(totalWidth * 0.31, 210, 380)) + 10;
            _savedDrawerLogicalWidth = drawerCol;
            RightPanelColumn.Width = new GridLength(drawerCol);
        }
        else
        {
            RightPanelColumn.Width = new GridLength(0);
        }
        HeaderTitleText.FontSize = Math.Round(15 * scale, 1);
        MonthTitle.FontSize = Math.Round(12 * scale, 1);
        MonthTitle.MinWidth = Math.Round(88 * scale);
        SelectedDateText.FontSize = Math.Round(13 * scale, 1);
        UndatedHeaderText.FontSize = Math.Round(11 * scale, 1);
        UndatedCountText.FontSize = Math.Round(10 * scale, 1);

        foreach (var child in WeekdayRow.Children)
        {
            if (child is TextBlock label)
            {
                label.FontSize = Math.Round(10.5 * scale, 1);
            }
        }
    }

    public void RefreshTheme()
    {
        if (_settings is null)
        {
            return;
        }

        ApplyThemeOnly();
        Refresh();
    }

    public void ApplyThemeOnly()
    {
        if (_settings is null)
        {
            return;
        }

        Root.RequestedTheme = Appearance.ToElementTheme((ThemeMode)_settings.Current.ThemeMode);
        ThemePalette.ApplyTextControlPalette(Root.Resources);
        RightPanelToggleButton.Foreground = (Brush)Application.Current.Resources[
            _rightPanelCollapsed ? "TextSecondaryBrush" : "AccentBrush"];
    }

    public void EnsureRefreshed()
    {
        if (Root.ActualWidth <= 0 || Root.ActualHeight <= 0)
        {
            Refresh();
            return;
        }

        if (Math.Abs(Root.ActualWidth - _lastLayoutWidth) >= 4 ||
            Math.Abs(Root.ActualHeight - _lastLayoutHeight) >= 4)
        {
            Refresh();
        }
    }

    public void ResetLayoutCache()
    {
        _lastLayoutWidth = 0;
        _lastLayoutHeight = 0;
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (Visibility == Visibility.Visible)
        {
            Refresh();
        }
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
                FontSize = 10.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources[i >= 5 ? "TextMutedBrush" : "TextSecondaryBrush"],
            };
            Grid.SetColumn(text, i);
            WeekdayRow.Children.Add(text);
        }
    }

    public void Refresh()
    {
        var viewModel = _viewModel;
        if (viewModel is null || _settings is null)
        {
            return;
        }

        ApplyResponsiveMetrics(GetUiScale());
        if (Root.ActualWidth > 0 && Root.ActualHeight > 0)
        {
            _lastLayoutWidth = Root.ActualWidth;
            _lastLayoutHeight = Root.ActualHeight;
        }

        MonthTitle.Text = $"{_month:yyyy 年 M 月}";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(MonthTitle, MonthTitle.Text);

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

        var undated = viewModel.AllItems
            .Where(item => item.DueAt is null && !item.IsDone)
            .Select(item => new CalendarEntry(item, null))
            .ToList();
        ShowUndated(undated);
    }

    private List<CalendarEntry> BuildEntries()
    {
        var entries = new List<CalendarEntry>();
        var viewModel = _viewModel;
        if (viewModel is null)
        {
            return entries;
        }

        foreach (var item in viewModel.AllItems)
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

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var gridHeight = DaysGrid.ActualHeight > 0
            ? DaysGrid.ActualHeight
            : Math.Max(MinLogicalHeight, _settings?.Current.CalendarHeight ?? DefaultLogicalHeight) - 104;
        var cellHeight = (gridHeight - 20) / 6.0;
        var maxChips = Math.Clamp((int)Math.Floor((cellHeight - 24 * scale) / (18 * scale)), 1, 6);

        var dayHeader = new Grid();
        dayHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dayHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var dayNumberText = new TextBlock
        {
            Text = date.Day.ToString(),
            FontSize = Math.Round(10.5 * scale, 1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = isToday
                ? new SolidColorBrush(Colors.White)
                : (Brush)Application.Current.Resources[inMonth ? "TextPrimaryBrush" : "TextMutedBrush"],
            FontWeight = (isToday || isSelected)
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal,
        };

        if (isToday)
        {
            var badgeSize = Math.Round(18 * scale);
            dayHeader.Children.Add(new Border
            {
                Width = badgeSize,
                Height = badgeSize,
                CornerRadius = new CornerRadius(badgeSize / 2),
                Background = (Brush)Application.Current.Resources["AccentBrush"],
                Child = dayNumberText,
            });
        }
        else
        {
            dayNumberText.Margin = new Thickness(2, 0, 0, 0);
            dayHeader.Children.Add(dayNumberText);
        }

        if (entries is { Count: > 0 } && entries.Count > maxChips)
        {
            var moreBadge = new TextBlock
            {
                Text = $"+{entries.Count - maxChips}",
                FontSize = Math.Round(9 * scale, 1),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
            };
            Grid.SetColumn(moreBadge, 1);
            dayHeader.Children.Add(moreBadge);
        }

        content.Children.Add(dayHeader);

        if (entries is { Count: > 0 })
        {
            var chips = new StackPanel
            {
                Spacing = 2,
                Margin = new Thickness(0, Math.Round(3 * scale), 0, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            Grid.SetRow(chips, 1);

            foreach (var entry in entries.Take(maxChips))
            {
                chips.Children.Add(BuildChip(entry, scale));
            }

            content.Children.Add(chips);
        }

        var cell = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(5, 4, 5, 4),
            BorderThickness = new Thickness(1),
            Background = GetCellBackground(isSelected, inMonth),
            BorderBrush = GetCellBorderBrush(isSelected, isToday, inMonth),
            Opacity = inMonth || isSelected ? 1 : 0.45,
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
            if (_rightPanelCollapsed)
            {
                SetRightPanelCollapsed(false, refresh: false);
            }

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

        cell.DragLeave += (_, _) => cell.Background = GetCellBackground(date == _selected, inMonth);

        cell.Drop += async (sender, args) =>
        {
            cell.Background = GetCellBackground(date == _selected, inMonth);
            if (_draggedEntry is not { } entry || sender is not FrameworkElement { Tag: DateTime target } || _viewModel is null)
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

            _rescheduleRequested?.Invoke(entry.Parent, target);
            Refresh();
        };

        cell.PointerEntered += (_, _) =>
            cell.Background = (Brush)Application.Current.Resources["HoverBrush"];
        cell.PointerExited += (_, _) =>
            cell.Background = GetCellBackground(date == _selected, inMonth);

        return cell;
    }

    private void UpdateSelectionVisuals()
    {
        foreach (var (date, cell) in _cells)
        {
            var inMonth = date.Month == _month.Month;
            var isSelected = date == _selected;
            var isToday = date == DateTime.Today;
            cell.Background = GetCellBackground(isSelected, inMonth);
            cell.BorderBrush = GetCellBorderBrush(isSelected, isToday, inMonth);
            cell.Opacity = inMonth || isSelected ? 1 : 0.45;
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

    private static Brush GetCellBackground(bool selected, bool inMonth) =>
        (Brush)Application.Current.Resources[
            selected ? "SegmentSelectedBrush" : inMonth ? "ItemCardBrush" : "TransparentBrush"];

    private static Brush GetCellBorderBrush(bool selected, bool isToday, bool inMonth) =>
        (Brush)Application.Current.Resources[
            selected || isToday ? "AccentBrush" : inMonth ? "ItemCardStrokeBrush" : "TransparentBrush"];

    private UIElement BuildChip(CalendarEntry entry, double scale)
    {
        var tagColor = TagColor(entry.TagIndex);
        var dark = _settings is null || Appearance.IsDark((ThemeMode)_settings.Current.ThemeMode);
        var pillColor = entry.TagIndex >= 0
            ? Color.FromArgb(0x28, tagColor.R, tagColor.G, tagColor.B)
            : dark ? Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x10, 0x00, 0x00, 0x00);

        var grid = new Grid
        {
            ColumnSpacing = Math.Round(3 * scale),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (entry.Subtask is not null)
        {
            grid.Children.Add(new TextBlock
            {
                Text = "\u21B3",
                FontSize = Math.Round(8.5 * scale, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(tagColor),
            });
        }
        else
        {
            var dotSize = Math.Max(4, Math.Round(5 * scale));
            grid.Children.Add(new Border
            {
                Width = dotSize,
                Height = dotSize,
                CornerRadius = new CornerRadius(dotSize / 2),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(tagColor),
            });
        }

        var title = new TextBlock
        {
            Text = entry.Title,
            FontSize = Math.Round(9.5 * scale, 1),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextDecorations = entry.IsDone ? Windows.UI.Text.TextDecorations.Strikethrough : Windows.UI.Text.TextDecorations.None,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
        };
        SetTitleToolTip(title, entry.FullTitle);
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);

        var chip = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 1.5, 4, 1.5),
            Background = new SolidColorBrush(pillColor),
            Opacity = entry.IsDone ? 0.5 : 1,
            CanDrag = true,
            Child = grid,
        };
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
        _selectedDayHasEntries = entries.Count > 0;
        UpdateRightPanelSplit();

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
        _undatedHasEntries = entries.Count > 0;
        UpdateRightPanelSplit();

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
        dialog.RequestedTheme = Root.RequestedTheme == ElementTheme.Default && _settings is not null
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
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, Math.Round(6 * scale), 8, Math.Round(6 * scale)),
            Background = (Brush)Application.Current.Resources["LayerFillSecondaryBrush"],
            CanDrag = true,
        };
        row.Tapped += async (_, _) => await ShowItemPreviewAsync(entry);
        row.PointerEntered += (_, _) =>
            row.Background = (Brush)Application.Current.Resources["HoverBrush"];
        row.PointerExited += (_, _) =>
            row.Background = (Brush)Application.Current.Resources["LayerFillSecondaryBrush"];
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

    private void UpdateRightPanelSplit()
    {
        if (_customRightSplit)
        {
            SelectedDayRow.Height = new GridLength(_rightSplitRatio, GridUnitType.Star);
            UndatedRow.Height = new GridLength(1 - _rightSplitRatio, GridUnitType.Star);
            return;
        }

        if (!_selectedDayHasEntries && _undatedHasEntries)
        {
            SelectedDayRow.Height = GridLength.Auto;
            UndatedRow.Height = new GridLength(1, GridUnitType.Star);
        }
        else if (_selectedDayHasEntries && !_undatedHasEntries)
        {
            SelectedDayRow.Height = new GridLength(1, GridUnitType.Star);
            UndatedRow.Height = GridLength.Auto;
        }
        else
        {
            SelectedDayRow.Height = new GridLength(1, GridUnitType.Star);
            UndatedRow.Height = new GridLength(1, GridUnitType.Star);
        }
    }

    private async Task ShowItemPreviewAsync(CalendarEntry entry)
    {
        if (_previewOpen)
        {
            return;
        }

        var settings = _settings;
        _previewOpen = true;
        try
        {
            var viewModel = _viewModel;
            if (settings is null || viewModel is null)
            {
                return;
            }

            var primaryBrush = (Brush)Application.Current.Resources["TextPrimaryBrush"];
            var mutedBrush = (Brush)Application.Current.Resources["TextMutedBrush"];
            var overdueBrush = (Brush)Application.Current.Resources["OverdueBrush"];

            var panel = new StackPanel { Spacing = 12 };

            var header = new Grid { ColumnSpacing = 8 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var completionBox = new CheckBox
            {
                IsChecked = entry.IsDone,
                Style = (Style)Application.Current.Resources["TodoCompletionCheckBoxStyle"],
                Margin = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(completionBox, "标记完成");
            header.Children.Add(completionBox);

            var tagDot = new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(TagColor(entry.TagIndex)),
            };
            Grid.SetColumn(tagDot, 1);
            header.Children.Add(tagDot);

            var title = new TextBlock
            {
                Text = entry.Title,
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = entry.IsDone ? mutedBrush : primaryBrush,
                TextDecorations = entry.IsDone
                    ? Windows.UI.Text.TextDecorations.Strikethrough
                    : Windows.UI.Text.TextDecorations.None,
            };
            Grid.SetColumn(title, 2);
            header.Children.Add(title);
            panel.Children.Add(header);

            if (entry.Subtask is not null)
            {
                AddPreviewRow(panel, "主任务", entry.Parent.Title);
                if (!string.IsNullOrWhiteSpace(entry.Subtask.Note))
                {
                    AddPreviewRow(panel, "备注", entry.Subtask.Note);
                }
            }

            if (!string.IsNullOrWhiteSpace(entry.Parent.Note))
            {
                AddPreviewRow(panel, entry.Subtask is not null ? "主任务备注" : "备注", entry.Parent.Note);
            }

            if (entry.TagIndex >= 0)
            {
                AddPreviewRow(panel, "标签", settings.GetTagName(entry.TagIndex));
            }

            var due = entry.DueAt;
            var timeValueText = AddPreviewRow(
                panel,
                "时间",
                due is { } dueValue ? dueValue.ToString("yyyy/M/d HH:mm") : "无截止时间",
                due is not null && entry.IsOverdue ? overdueBrush : null);
            var statusValueText = AddPreviewRow(
                panel,
                "状态",
                entry.IsDone ? "已完成" : entry.IsOverdue ? "已逾期" : "未完成",
                entry.IsOverdue ? overdueBrush : null);

            TextBlock? subtaskProgressText = null;
            var subtaskRows = new List<(SubtaskItem Subtask, CheckBox Check, TextBlock Title, TextBlock Time)>();

            void SyncPreviewVisuals()
            {
                completionBox.IsChecked = entry.IsDone;
                title.Foreground = entry.IsDone ? mutedBrush : primaryBrush;
                title.TextDecorations = entry.IsDone
                    ? Windows.UI.Text.TextDecorations.Strikethrough
                    : Windows.UI.Text.TextDecorations.None;
                timeValueText.Foreground = entry.DueAt is not null && entry.IsOverdue ? overdueBrush : primaryBrush;
                statusValueText.Text = entry.IsDone ? "已完成" : entry.IsOverdue ? "已逾期" : "未完成";
                statusValueText.Foreground = entry.IsOverdue ? overdueBrush : primaryBrush;

                if (subtaskProgressText is not null)
                {
                    subtaskProgressText.Text = entry.Parent.SubtaskProgressText;
                }

                foreach (var (subtask, subCheck, subTitle, subTime) in subtaskRows)
                {
                    subCheck.IsChecked = subtask.IsDone;
                    subTitle.Foreground = subtask.IsDone ? mutedBrush : primaryBrush;
                    subTitle.TextDecorations = subtask.IsDone
                        ? Windows.UI.Text.TextDecorations.Strikethrough
                        : Windows.UI.Text.TextDecorations.None;
                    subTime.Foreground = subtask.IsOverdue ? overdueBrush : mutedBrush;
                }
            }

            completionBox.Click += (_, _) =>
            {
                var done = completionBox.IsChecked == true;
                if (entry.Subtask is { } subtask)
                {
                    viewModel.SetSubtaskDone(entry.Parent, subtask, done);
                }
                else
                {
                    viewModel.SetDone(entry.Parent, done);
                }

                SyncPreviewVisuals();
                Refresh();
            };

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
                    subtaskProgressText = AddPreviewRow(panel, "子任务", parent.SubtaskProgressText);
                    var list = new StackPanel { Spacing = 6, Margin = new Thickness(0, 2, 0, 0) };
                    foreach (var subtask in parent.Subtasks)
                    {
                        var (row, check, subTitle, subTime) = BuildSubtaskPreviewRow(parent, subtask, () =>
                        {
                            SyncPreviewVisuals();
                            Refresh();
                        });
                        subtaskRows.Add((subtask, check, subTitle, subTime));
                        list.Children.Add(row);
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
                DefaultButton = ContentDialogButton.Primary,
            };
            dialog.RequestedTheme = Root.RequestedTheme == ElementTheme.Default
                ? Appearance.ToElementTheme((ThemeMode)settings.Current.ThemeMode)
                : Root.RequestedTheme;
            ThemePalette.ApplyTextControlPalette(dialog.Resources);
            dialog.Resources["ContentDialogBackground"] = Application.Current.Resources["DialogAcrylicBrush"];
            dialog.Resources["ContentDialogBorderBrush"] = Application.Current.Resources["CardStrokeBrush"];
            dialog.Resources["ContentDialogCornerRadius"] = new CornerRadius(8);
            dialog.Resources["ContentDialogSmokeFill"] = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x00, 0x00));

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                // 等对话框完全收掉再开编辑窗口，避免和这次点击的收尾抢输入队列。
                DispatcherQueue.TryEnqueue(() => _editRequested?.Invoke(entry.Parent));
            }
        }
        finally
        {
            _previewOpen = false;
        }
    }

    private static TextBlock AddPreviewRow(StackPanel panel, string label, string value, Brush? valueBrush = null)
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
        return valueText;
    }

    private (Grid Row, CheckBox Check, TextBlock Title, TextBlock Time) BuildSubtaskPreviewRow(
        TodoItem parent,
        SubtaskItem subtask,
        Action onChanged)
    {
        var primary = (Brush)Application.Current.Resources["TextPrimaryBrush"];
        var muted = (Brush)Application.Current.Resources["TextMutedBrush"];
        var overdue = (Brush)Application.Current.Resources["OverdueBrush"];

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var check = new CheckBox
        {
            IsChecked = subtask.IsDone,
            Style = (Style)Application.Current.Resources["TodoCompletionCheckBoxStyle"],
            Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(check, "子任务完成");
        check.Click += (_, _) =>
        {
            _viewModel?.SetSubtaskDone(parent, subtask, check.IsChecked == true);
            onChanged();
        };
        row.Children.Add(check);

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
        SetTitleToolTip(
            title,
            string.IsNullOrWhiteSpace(subtask.Note) ? subtask.Title : $"{subtask.Title}\n{subtask.Note}");
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

        return (row, check, title, time);
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

    private void MonthGrid_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(DaysGrid).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        _month = _month.AddMonths(delta > 0 ? -1 : 1);
        Refresh();
        e.Handled = true;
    }

    private void PrevMonth_DragOver(object sender, DragEventArgs e) =>
        StartMonthNavDrag(e, -1);

    private void NextMonth_DragOver(object sender, DragEventArgs e) =>
        StartMonthNavDrag(e, 1);

    private void MonthNav_DragLeave(object sender, DragEventArgs e) =>
        StopMonthNavDrag();

    private void StartMonthNavDrag(DragEventArgs e, int delta)
    {
        if (!e.DataView.Properties.ContainsKey(DragKey))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
            return;
        }

        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        e.DragUIOverride.IsCaptionVisible = false;
        e.DragUIOverride.IsGlyphVisible = false;

        if (_dragMonthNavDelta == delta && _dragMonthNavTimer is { IsRunning: true })
        {
            return;
        }

        _dragMonthNavDelta = delta;
        if (_dragMonthNavTimer is null)
        {
            _dragMonthNavTimer = DispatcherQueue.CreateTimer();
            _dragMonthNavTimer.Interval = TimeSpan.FromMilliseconds(480);
            _dragMonthNavTimer.Tick += (_, _) =>
            {
                if (_dragMonthNavDelta == 0)
                {
                    StopMonthNavDrag();
                    return;
                }

                _month = _month.AddMonths(_dragMonthNavDelta);
                Refresh();
            };
        }

        _dragMonthNavTimer.Start();
    }

    private void StopMonthNavDrag()
    {
        _dragMonthNavDelta = 0;
        _dragMonthNavTimer?.Stop();
    }

    private void MonthTitle_Click(object sender, RoutedEventArgs e)
    {
        var pickerYear = _month.Year;
        var flyout = new Flyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom,
        };

        var root = new StackPanel { Spacing = 8, Width = 196 };
        var yearHeader = new Grid();
        yearHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        yearHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        yearHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var prevYear = new Button
        {
            Content = new FontIcon { Glyph = "\uE76B", FontSize = 11 },
            Style = (Style)Application.Current.Resources["FlatIconButtonStyle"],
        };
        var yearText = new TextBlock
        {
            Text = $"{pickerYear} 年",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
        };
        var nextYear = new Button
        {
            Content = new FontIcon { Glyph = "\uE76C", FontSize = 11 },
            Style = (Style)Application.Current.Resources["FlatIconButtonStyle"],
        };
        Grid.SetColumn(yearText, 1);
        Grid.SetColumn(nextYear, 2);
        yearHeader.Children.Add(prevYear);
        yearHeader.Children.Add(yearText);
        yearHeader.Children.Add(nextYear);
        root.Children.Add(yearHeader);

        var monthsGrid = new Grid { RowSpacing = 4, ColumnSpacing = 4 };
        for (var r = 0; r < 3; r++)
        {
            monthsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (var c = 0; c < 4; c++)
        {
            monthsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var monthButtons = new List<Button>(12);
        void SyncMonthButtons()
        {
            yearText.Text = $"{pickerYear} 年";
            for (var i = 0; i < monthButtons.Count; i++)
            {
                var selected = pickerYear == _month.Year && (i + 1) == _month.Month;
                monthButtons[i].Background = (Brush)Application.Current.Resources[
                    selected ? "SegmentSelectedBrush" : "TransparentBrush"];
            }
        }

        for (var m = 1; m <= 12; m++)
        {
            var monthValue = m;
            var btn = new Button
            {
                Content = $"{m} 月",
                FontSize = 11,
                Height = 28,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(5),
                Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            };
            btn.Click += (_, _) =>
            {
                _month = new DateTime(pickerYear, monthValue, 1);
                flyout.Hide();
                Refresh();
            };
            Grid.SetRow(btn, (m - 1) / 4);
            Grid.SetColumn(btn, (m - 1) % 4);
            monthButtons.Add(btn);
            monthsGrid.Children.Add(btn);
        }

        prevYear.Click += (_, _) =>
        {
            pickerYear = Math.Max(2000, pickerYear - 1);
            SyncMonthButtons();
        };
        nextYear.Click += (_, _) =>
        {
            pickerYear = Math.Min(2100, pickerYear + 1);
            SyncMonthButtons();
        };

        SyncMonthButtons();
        root.Children.Add(monthsGrid);
        flyout.Content = root;
        flyout.ShowAt(MonthTitleButton);
    }

    private void Today_Click(object sender, RoutedEventArgs e)
    {
        _selected = DateTime.Today;
        _month = new DateTime(_selected.Year, _selected.Month, 1);
        Refresh();
    }

    private void NewTask_Click(object sender, RoutedEventArgs e) => _createRequested?.Invoke(_selected);

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
        StopMonthNavDrag();
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
        UndatedDropZone.Background = (Brush)Application.Current.Resources["ItemCardBrush"];

    private void UndatedDropZone_Drop(object sender, DragEventArgs e)
    {
        UndatedDropZone.Background = (Brush)Application.Current.Resources["ItemCardBrush"];

        if (!e.DataView.Properties.ContainsKey(DragKey) || _draggedEntry is not { } entry || _viewModel is null)
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

    private void RightPanelToggle_Click(object sender, RoutedEventArgs e) =>
        SetRightPanelCollapsed(!_rightPanelCollapsed);

    private void SetRightPanelCollapsed(bool collapsed, bool refresh = true)
    {
        if (_rightPanelCollapsed == collapsed)
        {
            return;
        }

        _rightPanelCollapsed = collapsed;
        RightPanelToggleIcon.Glyph = "\uE90D";
        RightPanelToggleButton.Foreground = (Brush)Application.Current.Resources[
            collapsed ? "TextSecondaryBrush" : "AccentBrush"];
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            RightPanelToggleButton, collapsed ? "展开详情" : "收起详情");

        var drawerVisual = ElementCompositionPreview.GetElementVisual(RightPanelGrid);
        var compositor = drawerVisual.Compositor;
        drawerVisual.StopAnimation("Translation");
        drawerVisual.StopAnimation("Opacity");

        if (collapsed)
        {
            var fallbackTotal = Root.ActualWidth > 0 ? Root.ActualWidth : DefaultLogicalWidth;
            _savedDrawerLogicalWidth = RightPanelColumn.Width.Value > 0
                ? RightPanelColumn.Width.Value
                : Math.Round(Math.Clamp(fallbackTotal * 0.31, 210, 380)) + 10;

            var ease = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.4f, 0f),
                new Vector2(1f, 1f));
            var slideAnim = compositor.CreateVector3KeyFrameAnimation();
            slideAnim.Duration = TimeSpan.FromMilliseconds(140);
            slideAnim.InsertKeyFrame(0f, Vector3.Zero);
            slideAnim.InsertKeyFrame(1f, new Vector3(36f, 0f, 0f), ease);

            var fadeAnim = compositor.CreateScalarKeyFrameAnimation();
            fadeAnim.Duration = TimeSpan.FromMilliseconds(120);
            fadeAnim.InsertKeyFrame(0f, 1f);
            fadeAnim.InsertKeyFrame(1f, 0f, ease);

            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (!_rightPanelCollapsed)
                {
                    return;
                }

                RightDrawerHost.Visibility = Visibility.Collapsed;
                drawerVisual.Properties.InsertVector3("Translation", Vector3.Zero);
                drawerVisual.Opacity = 1f;
                RightPanelColumn.Width = new GridLength(0);
                RightPanelCollapsedChanged?.Invoke(this, true);
                ApplyResponsiveMetrics(GetUiScale());
                if (refresh)
                {
                    Refresh();
                }
            });

            drawerVisual.StartAnimation("Translation", slideAnim);
            drawerVisual.StartAnimation("Opacity", fadeAnim);
            batch.End();
            return;
        }

        RightPanelCollapsedChanged?.Invoke(this, false);
        RightDrawerHost.Visibility = Visibility.Visible;
        drawerVisual.Opacity = 0f;
        drawerVisual.Properties.InsertVector3("Translation", new Vector3(36f, 0f, 0f));
        ApplyResponsiveMetrics(GetUiScale());
        if (refresh)
        {
            Refresh();
        }

        var openEase = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1f),
            new Vector2(0.3f, 1f));
        var openSlide = compositor.CreateVector3KeyFrameAnimation();
        openSlide.Duration = TimeSpan.FromMilliseconds(180);
        openSlide.InsertKeyFrame(0f, new Vector3(36f, 0f, 0f));
        openSlide.InsertKeyFrame(1f, Vector3.Zero, openEase);

        var openFade = compositor.CreateScalarKeyFrameAnimation();
        openFade.Duration = TimeSpan.FromMilliseconds(150);
        openFade.InsertKeyFrame(0f, 0f);
        openFade.InsertKeyFrame(1f, 1f, openEase);

        drawerVisual.StartAnimation("Translation", openSlide);
        drawerVisual.StartAnimation("Opacity", openFade);
    }

    private void HeaderBar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindAncestor<Button>(source) is not null)
        {
            return;
        }

        HeaderDragPressed?.Invoke(this, e);
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
            {
                return match;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    private void HeaderBar_PointerMoved(object sender, PointerRoutedEventArgs e) =>
        HeaderDragMoved?.Invoke(this, e);

    private void HeaderBar_PointerReleased(object sender, PointerRoutedEventArgs e) =>
        HeaderDragReleased?.Invoke(this, e);

    private void HeaderBar_PointerCaptureLost(object sender, PointerRoutedEventArgs e) =>
        HeaderDragCaptureLost?.Invoke(this, e);

    private void RightSplitHandle_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _hoveringRightSplitter = true;
        _setCursor?.Invoke(Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth);
    }

    private void RightSplitHandle_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _hoveringRightSplitter = false;
        if (!_splittingRightPanel)
        {
            _setCursor?.Invoke(null);
        }
    }

    private void RightSplitHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint((UIElement)sender).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _splittingRightPanel = ((UIElement)sender).CapturePointer(e.Pointer);
        _setCursor?.Invoke(Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth);
        e.Handled = true;
    }

    private void RightSplitHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_splittingRightPanel || RightPanelGrid.ActualHeight <= 80)
        {
            return;
        }

        var y = e.GetCurrentPoint(RightPanelGrid).Position.Y;
        _customRightSplit = true;
        _rightSplitRatio = Math.Clamp(y / RightPanelGrid.ActualHeight, 0.18, 0.82);
        UpdateRightPanelSplit();
        e.Handled = true;
    }

    private void RightSplitHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_splittingRightPanel)
        {
            return;
        }

        _splittingRightPanel = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        _setCursor?.Invoke(_hoveringRightSplitter ? Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth : null);
        e.Handled = true;
    }

    private void RightSplitHandle_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _splittingRightPanel = false;
        _hoveringRightSplitter = false;
        _setCursor?.Invoke(null);
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Visibility != Visibility.Visible)
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
}