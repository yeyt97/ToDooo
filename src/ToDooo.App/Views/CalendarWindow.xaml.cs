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

    private enum CalendarViewMode
    {
        Month = 0,
        Day = 1,
    }

    private DateTime _month;
    private DateTime _selected;
    private CalendarViewMode _viewMode = CalendarViewMode.Month;
    private CalendarViewMode _builtViewMode = CalendarViewMode.Month;
    private ScrollViewer? _activeScroll;
    private DateTime _monthGridStart;
    private int _monthLoadedWeeks;
    private double _monthRowStride;
    private DateTime _streamStart;
    private int _streamCount;
    private bool _suppressScrollSync;
    private DateTime? _pendingJumpDate;
    private double? _restoreScrollOffset;
    private bool _activeScrollReady;
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
    private readonly List<(DateTime Date, Border Cell, bool InScope)> _cells = new();

    public event EventHandler<PointerRoutedEventArgs>? HeaderDragPressed;
    public event EventHandler<PointerRoutedEventArgs>? HeaderDragMoved;
    public event EventHandler<PointerRoutedEventArgs>? HeaderDragReleased;
    public event EventHandler<PointerRoutedEventArgs>? HeaderDragCaptureLost;
    public event EventHandler<bool>? RightPanelCollapsedChanged;

    public bool IsRightPanelCollapsed => _rightPanelCollapsed;

    public bool IsPreviewOpen => _previewOpen;

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
            _viewModel.DataChanged -= OnViewModelDataChanged;
        }

        _viewModel = viewModel;
        _settings = settings;
        _editRequested = editRequested;
        _rescheduleRequested = rescheduleRequested;
        _createRequested = createRequested;
        _setCursor = setCursor;
        _viewMode = settings.Current.CalendarViewMode >= 1
            ? CalendarViewMode.Day
            : CalendarViewMode.Month;

        _viewModel.Items.CollectionChanged += OnItemsChanged;
        _viewModel.DataChanged += OnViewModelDataChanged;
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
        MonthTitle.MinWidth = Math.Round(80 * scale);
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
        UpdateHideCompletedVisuals();
        UpdateViewModeVisuals();
    }

    private void UpdateHideCompletedVisuals()
    {
        var hide = _settings?.Current.CalendarHideCompleted == true;
        HideCompletedIcon.Glyph = hide ? "\uED1A" : "\uE890";
        HideCompletedButton.Foreground = (Brush)Application.Current.Resources[
            hide ? "AccentBrush" : "TextSecondaryBrush"];
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            HideCompletedButton, hide ? "显示已完成" : "隐藏已完成");
    }

    private void HideCompleted_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null)
        {
            return;
        }

        _settings.Current.CalendarHideCompleted = !_settings.Current.CalendarHideCompleted;
        _settings.Save();
        UpdateHideCompletedVisuals();
        Refresh();
    }

    private void UpdateViewModeVisuals()
    {
        var selectedBrush = (Brush)Application.Current.Resources["SegmentSelectedBrush"];
        var transparentBrush = (Brush)Application.Current.Resources["TransparentBrush"];
        var activeText = (Brush)Application.Current.Resources["TextPrimaryBrush"];
        var mutedText = (Brush)Application.Current.Resources["TextMutedBrush"];

        void Apply(Button button, bool active)
        {
            button.Background = active ? selectedBrush : transparentBrush;
            button.Foreground = active ? activeText : mutedText;
        }

        Apply(MonthViewButton, _viewMode == CalendarViewMode.Month);
        Apply(DayViewButton, _viewMode == CalendarViewMode.Day);

        var (prevName, nextName) = _viewMode == CalendarViewMode.Day
            ? ("上一周", "下一周")
            : ("上个月", "下个月");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PrevNavButton, prevName);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(NextNavButton, nextName);
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

    private bool _refreshQueued;

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        QueueRefreshIfVisible();

    private void OnViewModelDataChanged(object? sender, EventArgs e) =>
        QueueRefreshIfVisible();

    private void QueueRefreshIfVisible()
    {
        if (Visibility != Visibility.Visible || _refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _refreshQueued = false;
            if (Visibility == Visibility.Visible)
            {
                Refresh();
            }
        });
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

        // 只从「已完成初始定位」的滚动条读取偏移：窗口缩放会连续触发两次重建，        // 此时新滚动条的 VerticalOffset 还是 0，记下来就会跳到缓冲最顶端（看着像格子全空）。
        if (_restoreScrollOffset is null && _pendingJumpDate is null && _activeScrollReady &&
            _activeScroll is not null && _builtViewMode == _viewMode)
        {
            _restoreScrollOffset = _activeScroll.VerticalOffset;
        }

        ApplyResponsiveMetrics(GetUiScale());
        UpdateViewModeVisuals();
        if (Root.ActualWidth > 0 && Root.ActualHeight > 0)
        {
            _lastLayoutWidth = Root.ActualWidth;
            _lastLayoutHeight = Root.ActualHeight;
        }

        MonthTitle.Text = FormatHeaderTitle();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(MonthTitle, MonthTitle.Text);

        var entries = BuildEntries();
        var dated = entries
            .Where(entry => entry.DueAt is not null)
            .GroupBy(entry => entry.DueAt!.Value.Date)
            .ToDictionary(group => group.Key, group => group
                .OrderBy(entry => entry.DueAt)
                .ThenBy(entry => entry.Title)
                .ToList());

        WeekdayRow.Visibility = _viewMode == CalendarViewMode.Month
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (_viewMode == CalendarViewMode.Day)
        {
            BuildDayAgendaCells(dated);
        }
        else
        {
            BuildMonthCells(dated);
        }

        _builtViewMode = _viewMode;
        _pendingJumpDate = null;
        ShowSelectedDay(dated.TryGetValue(_selected, out var dayEntries) ? dayEntries : new List<CalendarEntry>());

        var undated = viewModel.AllItems
            .Where(item => item.DueAt is null && !item.IsDone)
            .Select(item => new CalendarEntry(item, null))
            .ToList();
        ShowUndated(undated);
        UpdateWeeklySummary(viewModel);
    }

    private void UpdateWeeklySummary(MainViewModel viewModel)
    {
        var weekStart = WeekStart(DateTime.Today);
        var weekEnd = weekStart.AddDays(6);
        var doneCount = viewModel.AllItems.Count(item =>
            item.IsDone &&
            (item.CompletedAt ?? item.DueAt) is { } dt &&
            dt.Date >= weekStart &&
            dt.Date <= weekEnd);
        WeeklySummaryText.Text = $"本周已完成 {doneCount} 项";
    }

    private string FormatHeaderTitle()
    {
        if (_viewMode == CalendarViewMode.Day)
        {
            return FormatWeekTitle(WeekStart(_selected));
        }

        return $"{_month:yyyy 年 M 月}";
    }

    private static string FormatWeekTitle(DateTime start)
    {
        var end = start.AddDays(6);
        if (start.Year == end.Year && start.Month == end.Month)
        {
            return $"{start:yyyy 年 M 月 d}–{end.Day} 日";
        }

        if (start.Year == end.Year)
        {
            return $"{start:yyyy 年 M/d}–{end:M/d}";
        }

        return $"{start:yyyy/M/d}–{end:yyyy/M/d}";
    }

    private static DateTime WeekStart(DateTime date) =>
        date.Date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    private List<CalendarEntry> BuildEntries()
    {
        var entries = new List<CalendarEntry>();
        var viewModel = _viewModel;
        if (viewModel is null)
        {
            return entries;
        }

        var hideCompleted = _settings?.Current.CalendarHideCompleted == true;
        foreach (var item in viewModel.AllItems)
        {
            if (item.DueAt is not null && (!hideCompleted || !item.IsDone))
            {
                entries.Add(new CalendarEntry(item, null));
            }

            foreach (var subtask in item.Subtasks)
            {
                if (subtask.DueAt is not null && (!hideCompleted || (!item.IsDone && !subtask.IsDone)))
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

        DaysGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        DaysGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var gridHeight = DaysGrid.ActualHeight > 0
            ? DaysGrid.ActualHeight
            : Math.Max(MinLogicalHeight, _settings?.Current.CalendarHeight ?? DefaultLogicalHeight) - 104;
        const double rowSpacing = 4;
        var rowHeight = Math.Max(46, Math.Floor((gridHeight - rowSpacing * 5) / 6.0));
        _monthRowStride = rowHeight + rowSpacing;

        var keepExistingBuffer = _pendingJumpDate is null
            && _builtViewMode == CalendarViewMode.Month
            && _monthLoadedWeeks >= 12
            && _month >= _monthGridStart.AddDays(14)
            && _month <= _monthGridStart.AddDays((_monthLoadedWeeks - 6) * 7);

        if (!keepExistingBuffer)
        {
            var anchor = _pendingJumpDate ?? _month;
            _monthGridStart = WeekStart(new DateTime(anchor.Year, anchor.Month, 1)).AddDays(-42);
            _monthLoadedWeeks = 18;
        }

        var monthGrid = new Grid
        {
            RowSpacing = rowSpacing,
            ColumnSpacing = 4,
            Padding = new Thickness(0, 0, 10, 0),
        };
        for (var column = 0; column < 7; column++)
        {
            monthGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        void AppendWeeks(int startWeekIndex, int weekCount, Dictionary<DateTime, List<CalendarEntry>> datedMap)
        {
            for (var w = 0; w < weekCount; w++)
            {
                var rowIndex = startWeekIndex + w;
                monthGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(rowHeight) });
                for (var col = 0; col < 7; col++)
                {
                    var date = _monthGridStart.AddDays(rowIndex * 7 + col);
                    var cell = BuildCell(date, datedMap.TryGetValue(date, out var items) ? items : null, rowHeight);
                    Grid.SetRow(cell, rowIndex);
                    Grid.SetColumn(cell, col);
                    monthGrid.Children.Add(cell);
                    _cells.Add((date, cell, true));
                }
            }
        }

        AppendWeeks(0, _monthLoadedWeeks, dated);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            Content = monthGrid,
        };
        _activeScroll = scroll;
        _activeScrollReady = false;

        var targetWeek = WeekStart(_month);
        var targetRow = Math.Clamp((int)Math.Round((targetWeek - _monthGridStart).TotalDays / 7.0), 0, Math.Max(0, _monthLoadedWeeks - 6));
        var initialOffset = _restoreScrollOffset ?? (targetRow * _monthRowStride);
        _restoreScrollOffset = null;

        var initialApplied = false;
        var extendQueued = false;

        scroll.LayoutUpdated += (_, _) =>
        {
            if (initialApplied || scroll.ExtentHeight <= 0 || scroll.ViewportHeight <= 0)
            {
                return;
            }

            initialApplied = true;
            _suppressScrollSync = true;
            scroll.ChangeView(null, Math.Clamp(initialOffset, 0, scroll.ScrollableHeight), null, true);
            DispatcherQueue.TryEnqueue(() =>
            {
                _suppressScrollSync = false;
                if (ReferenceEquals(_activeScroll, scroll))
                {
                    _activeScrollReady = true;
                }
            });
        };

        scroll.ViewChanged += (_, args) =>
        {
            if (!initialApplied || _suppressScrollSync || _monthRowStride <= 0 || !ReferenceEquals(_activeScroll, scroll))
            {
                return;
            }

            var centerRow = Math.Clamp(
                (int)Math.Floor((scroll.VerticalOffset + scroll.ViewportHeight * 0.45) / _monthRowStride),
                0,
                Math.Max(0, _monthLoadedWeeks - 1));
            var centerDate = _monthGridStart.AddDays(centerRow * 7 + 3);
            var visibleMonth = new DateTime(centerDate.Year, centerDate.Month, 1);
            if (visibleMonth != _month)
            {
                _month = visibleMonth;
                MonthTitle.Text = FormatHeaderTitle();
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(MonthTitle, MonthTitle.Text);
                UpdateSelectionVisuals();
            }

            if (args.IsIntermediate || extendQueued)
            {
                return;
            }

            if (scroll.ScrollableHeight - scroll.VerticalOffset < _monthRowStride * 2.5)
            {
                extendQueued = true;
                DispatcherQueue.TryEnqueue(() =>
                {
                    extendQueued = false;
                    if (!ReferenceEquals(_activeScroll, scroll) || _viewMode != CalendarViewMode.Month)
                    {
                        return;
                    }

                    var startIdx = _monthLoadedWeeks;
                    _monthLoadedWeeks += 6;
                    var currentDated = BuildDatedLookup();
                    AppendWeeks(startIdx, 6, currentDated);
                    UpdateSelectionVisuals();
                });
                return;
            }

            if (scroll.VerticalOffset < _monthRowStride * 2.5)
            {
                extendQueued = true;
                var oldOffset = scroll.VerticalOffset;
                DispatcherQueue.TryEnqueue(() =>
                {
                    extendQueued = false;
                    if (!ReferenceEquals(_activeScroll, scroll) || _viewMode != CalendarViewMode.Month)
                    {
                        return;
                    }

                    _monthGridStart = _monthGridStart.AddDays(-42);
                    _monthLoadedWeeks += 6;
                    _restoreScrollOffset = oldOffset + 6 * _monthRowStride;
                    Refresh();
                });
            }
        };

        DaysGrid.Children.Add(scroll);
    }

    private Dictionary<DateTime, List<CalendarEntry>> BuildDatedLookup() =>
        BuildEntries()
            .Where(entry => entry.DueAt is not null)
            .GroupBy(entry => entry.DueAt!.Value.Date)
            .ToDictionary(group => group.Key, group => group
                .OrderBy(entry => entry.DueAt)
                .ThenBy(entry => entry.Title)
                .ToList());

    private void BuildDayAgendaCells(Dictionary<DateTime, List<CalendarEntry>> dated)
    {
        DaysGrid.Children.Clear();
        DaysGrid.RowDefinitions.Clear();
        DaysGrid.ColumnDefinitions.Clear();
        _cells.Clear();

        DaysGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        DaysGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var scale = GetUiScale();
        var keepExistingBuffer = _pendingJumpDate is null
            && _builtViewMode == CalendarViewMode.Day
            && _streamCount >= 21
            && _selected >= _streamStart.AddDays(7)
            && _selected <= _streamStart.AddDays(_streamCount - 8);

        if (!keepExistingBuffer)
        {
            var anchor = _pendingJumpDate ?? _selected;
            _streamStart = WeekStart(anchor).AddDays(-14);
            _streamCount = 35;
        }

        var stream = new StackPanel
        {
            Spacing = Math.Round(6 * scale),
            Padding = new Thickness(0, 0, 10, 0),
        };

        var weekdayNames = new[] { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };
        var dateColWidth = Math.Round(96 * scale);

        Border CreateWeekDayCard(DateTime date, Dictionary<DateTime, List<CalendarEntry>> datedMap)
        {
            var entries = datedMap.TryGetValue(date, out var items) ? items : null;
            var isSelected = date == _selected;
            var isToday = date == DateTime.Today;
            var dayOfWeekIdx = ((int)date.DayOfWeek + 6) % 7;

            var rowGrid = new Grid { ColumnSpacing = Math.Round(10 * scale) };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(dateColWidth) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dateStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = Math.Round(6 * scale),
                VerticalAlignment = entries is { Count: > 0 } ? VerticalAlignment.Top : VerticalAlignment.Center,
                Margin = entries is { Count: > 0 } ? new Thickness(0, 4, 0, 0) : new Thickness(0),
            };

            var dateText = new TextBlock
            {
                Text = $"{date:M/d}",
                FontSize = Math.Round(12 * scale, 1),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = isToday
                    ? new SolidColorBrush(Colors.White)
                    : (Brush)Application.Current.Resources["TextPrimaryBrush"],
            };

            if (isToday)
            {
                dateStack.Children.Add(new Border
                {
                    Padding = new Thickness(7, 2, 7, 2),
                    CornerRadius = new CornerRadius(10),
                    Background = (Brush)Application.Current.Resources["AccentBrush"],
                    Child = dateText,
                });
            }
            else
            {
                dateStack.Children.Add(dateText);
            }

            dateStack.Children.Add(new TextBlock
            {
                Text = weekdayNames[dayOfWeekIdx],
                FontSize = Math.Round(11 * scale, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources[dayOfWeekIdx >= 5 ? "TextMutedBrush" : "TextSecondaryBrush"],
            });

            rowGrid.Children.Add(dateStack);

            if (entries is { Count: > 0 })
            {
                var itemsPanel = new StackPanel { Spacing = Math.Round(4 * scale) };
                foreach (var entry in entries)
                {
                    itemsPanel.Children.Add(BuildDayEntryCard(entry, scale, compact: true));
                }

                Grid.SetColumn(itemsPanel, 1);
                rowGrid.Children.Add(itemsPanel);
            }
            else
            {
                var emptyText = new TextBlock
                {
                    Text = "无安排",
                    FontSize = Math.Round(11 * scale, 1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = 0.55,
                    Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
                };
                Grid.SetColumn(emptyText, 1);
                rowGrid.Children.Add(emptyText);
            }

            var quickAddBtn = new Button
            {
                Width = 22,
                Height = 22,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                VerticalAlignment = entries is { Count: > 0 } ? VerticalAlignment.Top : VerticalAlignment.Center,
                Opacity = 0,
                IsHitTestVisible = false,
                Style = (Style)Application.Current.Resources["FlatIconButtonStyle"],
                Content = new FontIcon { Glyph = "\uE710", FontSize = 10 },
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(quickAddBtn, "在此日期新建任务");
            var captureDate = date;
            quickAddBtn.Tapped += (_, args) => args.Handled = true;
            quickAddBtn.Click += (_, _) => _createRequested?.Invoke(captureDate);
            Grid.SetColumn(quickAddBtn, 2);
            rowGrid.Children.Add(quickAddBtn);

            var rowCard = new Border
            {
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, Math.Round(7 * scale), 10, Math.Round(7 * scale)),
                Margin = dayOfWeekIdx == 0 ? new Thickness(0, 6, 0, 0) : new Thickness(0),
                BorderThickness = new Thickness(1),
                Background = GetCellBackground(isSelected, true),
                BorderBrush = GetCellBorderBrush(isSelected, isToday, true),
                Child = rowGrid,
                AllowDrop = true,
            };
            AttachCellInteractions(rowCard, date, selectable: true);
            rowCard.PointerEntered += (_, _) =>
            {
                quickAddBtn.Opacity = 1;
                quickAddBtn.IsHitTestVisible = true;
            };
            rowCard.PointerExited += (_, _) =>
            {
                quickAddBtn.Opacity = 0;
                quickAddBtn.IsHitTestVisible = false;
            };
            return rowCard;
        }

        Border? scrollTarget = null;
        var targetDate = (_pendingJumpDate ?? _selected).Date;
        for (var i = 0; i < _streamCount; i++)
        {
            var date = _streamStart.AddDays(i);
            var card = CreateWeekDayCard(date, dated);
            stream.Children.Add(card);
            _cells.Add((date, card, true));
            if (date == targetDate)
            {
                scrollTarget = card;
            }
        }

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = stream,
        };
        _activeScroll = scroll;
        _activeScrollReady = false;
        var savedOffset = _restoreScrollOffset;
        _restoreScrollOffset = null;

        var initialApplied = false;
        var extendQueued = false;

        scroll.LayoutUpdated += (_, _) =>
        {
            if (initialApplied || scroll.ExtentHeight <= 0 || scroll.ViewportHeight <= 0)
            {
                return;
            }

            initialApplied = true;
            _suppressScrollSync = true;
            if (savedOffset is { } offset)
            {
                scroll.ChangeView(null, Math.Clamp(offset, 0, scroll.ScrollableHeight), null, true);
            }
            else if (scrollTarget is not null)
            {
                var pt = scrollTarget.TransformToVisual(stream).TransformPoint(new Windows.Foundation.Point(0, 0));
                scroll.ChangeView(null, Math.Clamp(pt.Y, 0, scroll.ScrollableHeight), null, true);
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                _suppressScrollSync = false;
                if (ReferenceEquals(_activeScroll, scroll))
                {
                    _activeScrollReady = true;
                }
            });
        };

        scroll.ViewChanged += (_, args) =>
        {
            if (!initialApplied || _suppressScrollSync || _cells.Count == 0 || !ReferenceEquals(_activeScroll, scroll))
            {
                return;
            }

            var probeY = scroll.VerticalOffset + 24;
            foreach (var (d, card, _) in _cells)
            {
                var pos = card.TransformToVisual(stream).TransformPoint(new Windows.Foundation.Point(0, 0));
                if (pos.Y + card.ActualHeight >= probeY)
                {
                    var weekText = FormatWeekTitle(WeekStart(d));
                    if (MonthTitle.Text != weekText)
                    {
                        MonthTitle.Text = weekText;
                        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(MonthTitle, weekText);
                    }

                    break;
                }
            }

            if (args.IsIntermediate || extendQueued)
            {
                return;
            }

            if (scroll.ScrollableHeight - scroll.VerticalOffset < 180)
            {
                extendQueued = true;
                DispatcherQueue.TryEnqueue(() =>
                {
                    extendQueued = false;
                    if (!ReferenceEquals(_activeScroll, scroll) || _viewMode != CalendarViewMode.Day)
                    {
                        return;
                    }

                    var currentDated = BuildDatedLookup();
                    var appendStart = _streamStart.AddDays(_streamCount);
                    _streamCount += 14;
                    for (var i = 0; i < 14; i++)
                    {
                        var date = appendStart.AddDays(i);
                        var card = CreateWeekDayCard(date, currentDated);
                        stream.Children.Add(card);
                        _cells.Add((date, card, true));
                    }
                });
                return;
            }

            if (scroll.VerticalOffset < 180)
            {
                extendQueued = true;
                var oldOffset = scroll.VerticalOffset;
                DispatcherQueue.TryEnqueue(() =>
                {
                    extendQueued = false;
                    if (!ReferenceEquals(_activeScroll, scroll) || _viewMode != CalendarViewMode.Day)
                    {
                        return;
                    }

                    var oldHeight = stream.ActualHeight;
                    var currentDated = BuildDatedLookup();
                    _streamStart = _streamStart.AddDays(-14);
                    _streamCount += 14;
                    for (var i = 13; i >= 0; i--)
                    {
                        var date = _streamStart.AddDays(i);
                        var card = CreateWeekDayCard(date, currentDated);
                        stream.Children.Insert(0, card);
                        _cells.Insert(0, (date, card, true));
                    }

                    scroll.UpdateLayout();
                    var deltaHeight = stream.ActualHeight - oldHeight;
                    if (deltaHeight > 0)
                    {
                        _suppressScrollSync = true;
                        scroll.ChangeView(null, oldOffset + deltaHeight, null, true);
                        DispatcherQueue.TryEnqueue(() => _suppressScrollSync = false);
                    }
                });
            }
        };

        DaysGrid.Children.Add(scroll);
    }

    private UIElement BuildDayEntryCard(CalendarEntry entry, double scale, bool compact = false)
    {
        var padY = Math.Round((compact ? 5.5 : 8) * scale);
        var padX = compact ? 8 : 10;
        var row = new Grid
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(padX, padY, padX, padY),
            Background = (Brush)Application.Current.Resources["LayerFillSecondaryBrush"],
            CanDrag = true,
        };
        row.Tapped += async (_, args) =>
        {
            args.Handled = true;
            await ShowItemPreviewAsync(entry);
        };
        row.PointerEntered += (_, _) =>
            row.Background = (Brush)Application.Current.Resources["HoverBrush"];
        row.PointerExited += (_, _) =>
            row.Background = (Brush)Application.Current.Resources["LayerFillSecondaryBrush"];
        row.DragStarting += (_, args) =>
        {
            _draggedEntry = entry;
            args.Data.Properties[DragKey] = true;
            args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        };
        row.DropCompleted += (_, _) => _draggedEntry = null;
        AttachEntryContextFlyout(row, entry);

        var content = new Grid { ColumnSpacing = 8 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var check = new CheckBox
        {
            IsChecked = entry.IsDone,
            Style = (Style)Application.Current.Resources["TodoCompletionCheckBoxStyle"],
            Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(check, "标记完成");
        check.Tapped += (_, args) => args.Handled = true;
        check.Click += async (_, _) =>
        {
            if (_viewModel is null)
            {
                return;
            }

            var done = check.IsChecked == true;
            if (entry.Subtask is { } subtask)
            {
                _viewModel.SetSubtaskDone(entry.Parent, subtask, done);
                Refresh();
                if (done && !_previewOpen && !entry.Parent.IsDone &&
                    entry.Parent.Subtasks.Count > 0 && entry.Parent.Subtasks.All(s => s.IsDone))
                {
                    await PromptCompleteParentAsync(entry.Parent);
                }
            }
            else
            {
                _viewModel.SetDone(entry.Parent, done);
                Refresh();
            }
        };
        content.Children.Add(check);

        var dotSize = Math.Max(6, Math.Round(7.5 * scale));
        var dot = new Border
        {
            Width = dotSize,
            Height = dotSize,
            CornerRadius = new CornerRadius(dotSize / 2),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(TagColor(entry.TagIndex)),
        };
        Grid.SetColumn(dot, 1);
        content.Children.Add(dot);

        var textStack = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var title = new TextBlock
        {
            Text = entry.Title,
            FontSize = Math.Round(12.5 * scale, 1),
            MaxLines = 2,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)Application.Current.Resources[entry.IsDone ? "TextMutedBrush" : "TextPrimaryBrush"],
            TextDecorations = entry.IsDone
                ? Windows.UI.Text.TextDecorations.Strikethrough
                : Windows.UI.Text.TextDecorations.None,
        };
        textStack.Children.Add(title);

        if (entry.Subtask is not null)
        {
            textStack.Children.Add(new TextBlock
            {
                Text = $"\u21B3 {entry.Parent.Title}",
                FontSize = Math.Round(10.5 * scale, 1),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
            });
        }

        var note = entry.Subtask?.Note ?? entry.Parent.Note;
        if (!string.IsNullOrWhiteSpace(note))
        {
            textStack.Children.Add(new TextBlock
            {
                Text = note,
                FontSize = Math.Round(10.5 * scale, 1),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
            });
        }

        Grid.SetColumn(textStack, 2);
        content.Children.Add(textStack);

        var time = new TextBlock
        {
            Text = entry.DueAt?.ToString("HH:mm") ?? string.Empty,
            FontSize = Math.Round(11.5 * scale, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources[entry.IsOverdue ? "OverdueBrush" : "TextMutedBrush"],
        };
        Grid.SetColumn(time, 3);
        content.Children.Add(time);

        row.Children.Add(content);
        return row;
    }

    private async Task PromptCompleteParentAsync(TodoItem parent)
    {
        if (Root.XamlRoot is null || _viewModel is null || _settings is null || _previewOpen)
        {
            return;
        }

        _previewOpen = true;
        try
        {
            var dialog = CreateEntryDialog(
                "子任务已全部完成",
                $"「{parent.Title}」的子任务已全部完成，是否同时完成主任务？",
                "完成主任务");
            dialog.CloseButtonText = "暂不";

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                _viewModel.SetDone(parent, true);
                Refresh();
            }
        }
        finally
        {
            _previewOpen = false;
        }
    }

    private Border BuildCell(DateTime date, List<CalendarEntry>? entries, double cellHeight)
    {
        var scale = GetUiScale();
        var inMonth = date.Month == _month.Month;
        var isSelected = date == _selected;
        var isToday = date == DateTime.Today;

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var maxChips = Math.Clamp((int)Math.Floor((cellHeight - 24 * scale) / (18 * scale)), 1, 6);

        var dayHeader = new Grid();
        dayHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dayHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var dayText = date.Day == 1 ? $"{date.Month}/1" : date.Day.ToString();
        var dayNumberText = new TextBlock
        {
            Text = dayText,
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
                MinWidth = badgeSize,
                Height = badgeSize,
                Padding = date.Day == 1 ? new Thickness(4, 0, 4, 0) : new Thickness(0),
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
            Opacity = inMonth || isSelected ? 1 : 0.62,
            Child = content,
            AllowDrop = true,
        };

        AttachCellInteractions(cell, date, selectable: true);
        return cell;
    }

    private bool IsInCurrentScope(DateTime date) =>
        _viewMode != CalendarViewMode.Month || date.Month == _month.Month;

    private void AttachCellInteractions(Border cell, DateTime date, bool selectable)
    {
        var normalBrush = selectable
            ? GetCellBackground(date == _selected, IsInCurrentScope(date))
            : (Brush)Application.Current.Resources["ItemCardBrush"];

        cell.Tag = date;
        if (selectable)
        {
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

                if (_viewMode == CalendarViewMode.Month &&
                    (tapped.Year != _month.Year || tapped.Month != _month.Month))
                {
                    _month = new DateTime(tapped.Year, tapped.Month, 1);
                    MonthTitle.Text = FormatHeaderTitle();
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(MonthTitle, MonthTitle.Text);
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
                    _createRequested?.Invoke(tapped);
                }
            };

            cell.PointerEntered += (_, _) =>
                cell.Background = (Brush)Application.Current.Resources["HoverBrush"];
            cell.PointerExited += (_, _) =>
                cell.Background = GetCellBackground(date == _selected, IsInCurrentScope(date));
        }

        cell.DragOver += (_, args) =>
        {
            args.AcceptedOperation = args.DataView.Properties.ContainsKey(DragKey)
                ? Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move
                : Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
            args.DragUIOverride.IsCaptionVisible = false;
            args.DragUIOverride.IsGlyphVisible = false;
            cell.Background = (Brush)Application.Current.Resources["HoverBrush"];
        };

        cell.DragLeave += (_, _) =>
            cell.Background = selectable ? GetCellBackground(date == _selected, IsInCurrentScope(date)) : normalBrush;

        cell.Drop += async (sender, args) =>
        {
            cell.Background = selectable ? GetCellBackground(date == _selected, IsInCurrentScope(date)) : normalBrush;
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
    }

    private void UpdateSelectionVisuals()
    {
        foreach (var (date, cell, _) in _cells)
        {
            var inScope = IsInCurrentScope(date);
            var isSelected = date == _selected;
            var isToday = date == DateTime.Today;
            cell.Background = GetCellBackground(isSelected, inScope);
            cell.BorderBrush = GetCellBorderBrush(isSelected, isToday, inScope);
            cell.Opacity = inScope || isSelected ? 1 : 0.62;
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
        AttachEntryContextFlyout(chip, entry);

        return chip;
    }

    private void AttachEntryContextFlyout(FrameworkElement host, CalendarEntry entry)
    {
        var flyout = new MenuFlyout();

        var toggleDone = new MenuFlyoutItem { Text = entry.IsDone ? "标记为未完成" : "标记为完成" };
        toggleDone.Click += (_, _) =>
        {
            if (_viewModel is null)
            {
                return;
            }

            if (entry.Subtask is { } subtask)
            {
                _viewModel.SetSubtaskDone(entry.Parent, subtask, !entry.IsDone);
            }
            else
            {
                _viewModel.SetDone(entry.Parent, !entry.IsDone);
            }

            Refresh();
        };
        flyout.Items.Add(toggleDone);
        flyout.Items.Add(new MenuFlyoutSeparator());

        var today = DateTime.Today;
        flyout.Items.Add(BuildMoveItem("改到今天", today, entry));
        flyout.Items.Add(BuildMoveItem("改到明天", today.AddDays(1), entry));

        var clearDue = new MenuFlyoutItem { Text = "清除时间" };
        clearDue.Click += (_, _) =>
        {
            if (_viewModel is null)
            {
                return;
            }

            if (entry.Subtask is { } subtask)
            {
                _viewModel.SetSubtaskDue(entry.Parent, subtask, null);
            }
            else
            {
                _viewModel.ClearDue(entry.Parent);
            }

            Refresh();
        };
        flyout.Items.Add(clearDue);
        flyout.Items.Add(new MenuFlyoutSeparator());

        var edit = new MenuFlyoutItem { Text = "编辑…" };
        edit.Click += (_, _) => _editRequested?.Invoke(entry.Parent);
        flyout.Items.Add(edit);

        var delete = new MenuFlyoutItem { Text = "删除" };
        delete.Click += async (_, _) => await DeleteCalendarEntryAsync(entry);
        flyout.Items.Add(delete);

        host.ContextFlyout = flyout;
    }

    private MenuFlyoutItem BuildMoveItem(string text, DateTime target, CalendarEntry entry)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) =>
        {
            if (_viewModel is null)
            {
                return;
            }

            if (entry.Subtask is { } subtask)
            {
                var time = subtask.DueAt?.TimeOfDay ?? RoundToFiveMinutes(DateTime.Now.TimeOfDay);
                _viewModel.SetSubtaskDue(entry.Parent, subtask, target.Date + time);
            }
            else if (entry.Parent.DueAt is not null)
            {
                _rescheduleRequested?.Invoke(entry.Parent, target);
            }
            else
            {
                _viewModel.ScheduleAt(entry.Parent, target.Date + RoundToFiveMinutes(DateTime.Now.TimeOfDay));
            }

            Refresh();
        };
        return item;
    }

    private async Task DeleteCalendarEntryAsync(CalendarEntry entry)
    {
        if (Root.XamlRoot is null || _viewModel is null || _previewOpen)
        {
            return;
        }

        _previewOpen = true;
        try
        {
            var name = entry.Subtask is { } subtask
                ? $"{entry.Parent.Title} › {subtask.Title}"
                : entry.Parent.Title;
            var dialog = CreateEntryDialog("删除待办", $"确定删除「{name}」吗？", "删除");
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            if (entry.Subtask is { } target)
            {
                _viewModel.DeleteSubtask(entry.Parent, target);
            }
            else
            {
                _viewModel.Delete(entry.Parent);
            }

            Refresh();
        }
        finally
        {
            _previewOpen = false;
        }
    }

    private ContentDialog CreateEntryDialog(string title, string message, string primaryText)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = title,
            Content = message,
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.RequestedTheme = Root.RequestedTheme == ElementTheme.Default && _settings is not null
            ? Appearance.ToElementTheme((ThemeMode)_settings.Current.ThemeMode)
            : Root.RequestedTheme;
        var dark = dialog.RequestedTheme == ElementTheme.Dark;
        ThemePalette.ApplyTextControlPalette(dialog.Resources);
        dialog.Resources["ContentDialogBackground"] = ThemePalette.CreateDialogAcrylicBrush(dark);
        dialog.Resources["ContentDialogBorderBrush"] = Application.Current.Resources["CardStrokeBrush"];
        dialog.Resources["ContentDialogCornerRadius"] = new CornerRadius(8);
        dialog.Resources["ContentDialogSmokeFill"] = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x00, 0x00));
        return dialog;
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
        var dark = dialog.RequestedTheme == ElementTheme.Dark;
        ThemePalette.ApplyTextControlPalette(dialog.Resources);
        dialog.Resources["ContentDialogBackground"] = ThemePalette.CreateDialogAcrylicBrush(dark);
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
            var dark = dialog.RequestedTheme == ElementTheme.Dark;
            ThemePalette.ApplyTextControlPalette(dialog.Resources);
            dialog.Resources["ContentDialogBackground"] = ThemePalette.CreateDialogAcrylicBrush(dark);
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

    private void MonthView_Click(object sender, RoutedEventArgs e) =>
        SetViewMode(CalendarViewMode.Month);

    private void DayView_Click(object sender, RoutedEventArgs e) =>
        SetViewMode(CalendarViewMode.Day);

    private void SetViewMode(CalendarViewMode mode)
    {
        if (_viewMode == mode)
        {
            return;
        }

        _viewMode = mode;
        if (_settings is not null)
        {
            _settings.Current.CalendarViewMode = (int)mode;
            _settings.Save();
        }

        if (_viewMode != CalendarViewMode.Month &&
            (_selected.Year != _month.Year || _selected.Month != _month.Month))
        {
            var today = DateTime.Today;
            _selected = _month.Year == today.Year && _month.Month == today.Month
                ? today
                : _month;
        }

        _restoreScrollOffset = null;
        _pendingJumpDate = _viewMode == CalendarViewMode.Month ? _month : _selected;
        Refresh();
    }

    private void StepPeriod(int delta, bool animate = true)
    {
        if (_viewMode == CalendarViewMode.Day)
        {
            _selected = _selected.AddDays(delta * 7);
            _month = new DateTime(_selected.Year, _selected.Month, 1);
            var targetWeek = WeekStart(_selected);
            if (TrySmoothScrollToDate(targetWeek, animate))
            {
                MonthTitle.Text = FormatWeekTitle(targetWeek);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(MonthTitle, MonthTitle.Text);
                UpdateSelectionVisuals();
                ShowDetailsForSelected();
                return;
            }

            _restoreScrollOffset = null;
            _pendingJumpDate = targetWeek;
            Refresh();
            return;
        }

        _month = _month.AddMonths(delta);
        if (_activeScroll is { } scroll && _monthRowStride > 0)
        {
            var targetWeek = WeekStart(_month);
            var targetRow = (int)Math.Round((targetWeek - _monthGridStart).TotalDays / 7.0);
            if (targetRow >= 0 && targetRow + 5 < _monthLoadedWeeks)
            {
                var targetY = Math.Clamp(targetRow * _monthRowStride, 0, scroll.ScrollableHeight);
                scroll.ChangeView(null, targetY, null, disableAnimation: !animate);
                MonthTitle.Text = FormatHeaderTitle();
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(MonthTitle, MonthTitle.Text);
                UpdateSelectionVisuals();
                return;
            }
        }

        _restoreScrollOffset = null;
        _pendingJumpDate = _month;
        Refresh();
    }

    private bool TrySmoothScrollToDate(DateTime date, bool animate = true)
    {
        if (_activeScroll is not { } scroll || _activeScroll.Content is not FrameworkElement root)
        {
            return false;
        }

        foreach (var (d, card, _) in _cells)
        {
            if (d == date)
            {
                var pt = card.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(0, 0));
                scroll.ChangeView(null, Math.Clamp(pt.Y, 0, scroll.ScrollableHeight), null, disableAnimation: !animate);
                return true;
            }
        }

        return false;
    }

    private void PrevMonth_Click(object sender, RoutedEventArgs e) => StepPeriod(-1);

    private void NextMonth_Click(object sender, RoutedEventArgs e) => StepPeriod(1);

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
            _dragMonthNavTimer.Tick += (_, _) =>
            {
                if (_dragMonthNavDelta == 0)
                {
                    StopMonthNavDrag();
                    return;
                }

                _dragMonthNavTimer.Interval = TimeSpan.FromMilliseconds(720);
                StepPeriod(_dragMonthNavDelta, animate: false);
            };
        }

        _dragMonthNavTimer.Interval = TimeSpan.FromMilliseconds(520);
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
                if (_viewMode != CalendarViewMode.Month)
                {
                    var day = Math.Min(_selected.Day, DateTime.DaysInMonth(pickerYear, monthValue));
                    _selected = new DateTime(pickerYear, monthValue, day);
                }

                _restoreScrollOffset = null;
                _pendingJumpDate = _viewMode == CalendarViewMode.Month ? _month : _selected;
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
        _restoreScrollOffset = null;
        _pendingJumpDate = _viewMode == CalendarViewMode.Month ? _month : _selected;
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