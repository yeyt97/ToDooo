using System.Collections.Specialized;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Dispatching;
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
    private CalendarSyncService? _calendarSync;
    private Action<TodoItem>? _editRequested;
    private Action<TodoItem, DateTime>? _rescheduleRequested;
    private Action<DateTime>? _createRequested;
    private Action<Microsoft.UI.Input.InputSystemCursorShape?>? _setCursor;
    private Action<CalendarEvent>? _eventRequested;
    private const string DragKey = "ToDoooCalendarTask";

    // 月视图里跨日色条与单日 chip 共用同一组左缘度量，保证竖线中心、正文左缘完全对齐。
    private const double GridEventSlotWidth = 9;
    private const double GridEventGap = 4;
    private const double GridEventStripeWidth = 2.5;
    private CalendarEntry? _draggedEntry;

    /// <summary>正在格子间拖动的单次日程；与待办分开存，便于 Drop 时分辨对象类型。</summary>
    private CalendarEvent? _draggedEvent;
    private bool _previewOpen;

    /// <summary>当前窗口内要显示的事件，按开始时间排序；由同步结果填充。</summary>
    private List<CalendarEvent> _events = new();
    private CancellationTokenSource? _syncCts;

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

    /// <summary>上一次 DaysGrid 真正布局后得到的有效高度，用于重建瞬间兜底，避免行高跳变。</summary>
    private double _lastStableDaysHeight;
    private bool _rightPanelCollapsed;

    /// <summary>窗口被手动拖窄到这个宽度以下时自动收起侧栏；之后不会自动展开，只能手动点开。</summary>
    private const double AutoCollapseWidthLogical = 580;
    private double? _calendarWidthDuringDrawerResize;
    private double? _pendingDrawerResizeWidth;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _drawerResizeFallback;
    private double _savedDrawerLogicalWidth = DefaultDrawerLogicalWidth;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _dragMonthNavTimer;
    private int _dragMonthNavDelta;
    private readonly List<(DateTime Date, Border Cell, bool InScope)> _cells = new();

    public event EventHandler<PointerRoutedEventArgs>? HeaderDragPressed;
    public event EventHandler<PointerRoutedEventArgs>? HeaderDragMoved;
    public event EventHandler<PointerRoutedEventArgs>? HeaderDragReleased;
    public event EventHandler<PointerRoutedEventArgs>? HeaderDragCaptureLost;
    public event EventHandler<bool>? RightPanelCollapsedChanged;

    /// <summary>宿主窗口是否处于置顶（贴边停靠时是）。日程弹窗要跟着置顶，否则会被日历盖住。</summary>
    public Func<bool>? IsHostTopmost { get; set; }

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
        Root.LayoutUpdated += (_, _) => TraceDrawerToggle("layout");
    }

    public void Attach(
        MainViewModel viewModel,
        SettingsService settings,
        Action<TodoItem> editRequested,
        Action<TodoItem, DateTime> rescheduleRequested,
        Action<DateTime> createRequested,
        Action<Microsoft.UI.Input.InputSystemCursorShape?> setCursor,
        CalendarSyncService? calendarSync = null,
        Action<CalendarEvent>? eventRequested = null)
    {
        if (_viewModel is not null)
        {
            _viewModel.Items.CollectionChanged -= OnItemsChanged;
            _viewModel.DataChanged -= OnViewModelDataChanged;
        }

        if (_calendarSync is not null)
        {
            _calendarSync.EventsChanged -= OnCalendarEventsChanged;
            _calendarSync.SyncingChanged -= OnCalendarSyncingChanged;
        }

        _viewModel = viewModel;
        _settings = settings;
        _editRequested = editRequested;
        _rescheduleRequested = rescheduleRequested;
        _createRequested = createRequested;
        _setCursor = setCursor;
        _calendarSync = calendarSync;
        _eventRequested = eventRequested;
        _viewMode = settings.Current.CalendarViewMode >= 1
            ? CalendarViewMode.Day
            : CalendarViewMode.Month;

        _viewModel.Items.CollectionChanged += OnItemsChanged;
        _viewModel.DataChanged += OnViewModelDataChanged;
        if (_calendarSync is not null)
        {
            _calendarSync.EventsChanged += OnCalendarEventsChanged;
            _calendarSync.SyncingChanged += OnCalendarSyncingChanged;
            SetSyncSpinner(_calendarSync.IsSyncing);
            UpdateSyncPendingIndicator();
        }

        RefreshTheme();

        if (_calendarSync is not null)
        {
            DispatcherQueue.TryEnqueue(() => _ = SyncAndRefreshAsync());
        }
    }

    private void OnCalendarSyncingChanged(object? sender, bool syncing) =>
        DispatcherQueue.TryEnqueue(() => SetSyncSpinner(syncing));

    /// <summary>同步时让同步图标匀速旋转，充当「菊花」进度。</summary>
    private void SetSyncSpinner(bool spinning)
    {
        if (SyncIcon is null || SyncButton is null || SyncSpinnerHost is null)
        {
            return;
        }

        // 旋转的是一个固定 16×16 的宿主，圆心恒为几何中心，避免首次布局未完成时绕左上角乱甩。
        var visual = ElementCompositionPreview.GetElementVisual(SyncSpinnerHost);
        SyncButton.IsEnabled = !spinning;
        if (!spinning)
        {
            visual.StopAnimation("RotationAngleInDegrees");
            visual.RotationAngleInDegrees = 0f;
            return;
        }

        visual.CenterPoint = new Vector3(8f, 8f, 0);
        var spin = visual.Compositor.CreateScalarKeyFrameAnimation();
        spin.Duration = TimeSpan.FromMilliseconds(900);
        spin.InsertKeyFrame(0f, 0f);
        spin.InsertKeyFrame(1f, 360f);
        spin.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
        visual.StartAnimation("RotationAngleInDegrees", spin);
    }

    private void SyncButton_Click(object sender, RoutedEventArgs e) => _ = RefreshCalendarAsync();

    /// <summary>
    /// 有待补传的改动时，在同步图标右下角点一个橙色小圆点，悬停可看原因。
    /// 不再弹窗：拖几条日程就该安静地排队，而不是每次都打断操作。
    /// </summary>
    private void UpdateSyncPendingIndicator(string? reason = null)
    {
        if (SyncPendingDot is null || SyncButton is null || _calendarSync is null)
        {
            return;
        }

        var pending = _calendarSync.PendingCount;
        _syncPendingReason = pending > 0
            ? (_calendarSync.LastPushError ?? reason ?? _syncPendingReason ??
               "有改动还没同步到云端，稍后会自动重试。")
            : null;

        SyncPendingDot.Visibility = pending > 0 ? Visibility.Visible : Visibility.Collapsed;
        var tip = pending > 0
            ? $"{_syncPendingReason}\n待同步 {pending} 项"
            : "同步日历";
        ToolTipService.SetToolTip(SyncButton, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(SyncButton, tip);
    }

    private string? _syncPendingReason;

    private Action? _closeRequested;

    public void SetCloseRequested(Action closeRequested) => _closeRequested = closeRequested;

    private void CloseCalendar_Click(object sender, RoutedEventArgs e) => _closeRequested?.Invoke();

    private void OnCalendarEventsChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            LoadCachedEvents();
            QueueRefreshIfVisible();
        });

    /// <summary>从母事件按窗口展开出当前月份前后各一个月的事件。</summary>
    private void LoadCachedEvents()
    {
        if (_calendarSync is null)
        {
            _events = new List<CalendarEvent>();
            return;
        }

        try
        {
            var from = _month.AddMonths(-1);
            var to = _month.AddMonths(2);
            _events = _calendarSync.LoadEvents(from, to);
        }
        catch
        {
            // 缓存读取失败时日历照常显示待办，不影响主流程
            _events = new List<CalendarEvent>();
        }
    }

    /// <summary>同步远端并刷新。窗口取前后一个月，覆盖月视图滚动缓冲与日视图。</summary>
    private async Task SyncAndRefreshAsync()
    {
        if (_calendarSync is null || !_calendarSync.IsConfigured)
        {
            LoadCachedEvents();
            QueueRefreshIfVisible();
            return;
        }

        _syncCts?.Cancel();
        _syncCts = new CancellationTokenSource();
        var token = _syncCts.Token;

        var from = _month.AddMonths(-1);
        var to = _month.AddMonths(2);
        try
        {
            var result = await _calendarSync.SyncAsync(from, to, null, token);
            if (result.Succeeded)
            {
                LoadCachedEvents();
                QueueRefreshIfVisible();
            }

            // 同步跑完，队列可能已清空：同步更新小点与提示。
            UpdateSyncPendingIndicator();
        }
        catch (OperationCanceledException)
        {
            // 用户切换月份导致取消，忽略
        }
    }

    public Task RefreshCalendarAsync() => SyncAndRefreshAsync();

    /// <summary>供外部（标题栏按钮等）打开某条日程的详情。</summary>
    public Task ShowEventDetailAsync(CalendarEvent evt) => ShowEventPreviewAsync(evt);

    /// <summary>
    /// 在某天新建一条 iCloud 日程。表单交给独立的 CalendarEventDialog，
    /// 创建成功后再回写缓存并刷新日历。
    /// </summary>
    public async Task CreateEventAsync(DateTime date)
    {
        if (_calendarSync is null || _settings is null)
        {
            return;
        }

        // 离线也能建：只要本地有日历集合就行，改动会排进待补传队列。
        if (_calendarSync.Repository.GetSources().Count == 0)
        {
            await ShowMessageAsync("还没有可用日历",
                _calendarSync.IsConfigured
                    ? "先同步一次拿到日历列表，才能新建日程。"
                    : "先到设置里打开日历同步并连接 iCloud，拿到日历列表后就能新建日程。");
            return;
        }

        var dialog = new CalendarEventDialog(_settings, _calendarSync, date);
        dialog.ShowOver(null, topmost: IsHostTopmost?.Invoke() == true);
        var created = await dialog.Result;
        if (!created || dialog.Draft is not { } draft || dialog.TargetCalendarHref is not { } href)
        {
            return;
        }

        try
        {
            await _calendarSync.CreateEventAsync(href, draft);
            LoadCachedEvents();
            Refresh();
        }
        catch (CalDavPendingException pending)
        {
            LoadCachedEvents();
            Refresh();
            UpdateSyncPendingIndicator(pending.Message);
        }
        catch (CalDavException exception)
        {
            await ShowMessageAsync("新建日程失败", exception.Message);
        }
    }

    public void BeginExternalDrag(
        TodoItem parent,
        SubtaskItem? subtask,
        Windows.ApplicationModel.DataTransfer.DataPackage data)
    {
        _draggedEntry = new CalendarEntry(parent, subtask);
        _draggedEvent = null;
        data.Properties[DragKey] = true;
        data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
    }

    public void EndExternalDrag()
    {
        _draggedEntry = null;
        _draggedEvent = null;
    }

    private double GetUiScale()
    {
        // 缩放比必须以「含抽屉的总宽」为基准，否则抽屉开合改的是实际宽度、基准也跟着变，
        // 一除下来比例会飘，表现为开关抽屉时字体大小变一下。
        var drawerWidth = _rightPanelCollapsed ? _savedDrawerLogicalWidth : 0;
        var fallbackWidth = _settings is not null
            ? Math.Max(MinLogicalWidth, _settings.Current.CalendarWidth)
            : DefaultLogicalWidth;
        var fallbackHeight = _settings is not null ? Math.Max(MinLogicalHeight, _settings.Current.CalendarHeight) : DefaultLogicalHeight;
        var width = (Root.ActualWidth > 0 ? Root.ActualWidth : fallbackWidth) + drawerWidth;
        var height = Root.ActualHeight > 0 ? Root.ActualHeight : fallbackHeight;
        return Math.Clamp(Math.Min(width / DefaultLogicalWidth, height / DefaultLogicalHeight), 0.82, 1.55);
    }

    /// <summary>
    /// 详情抽屉固定在日历右侧，不随吸附方向换边。
    /// 之前按吸附侧把抽屉换到左边，会让左侧吸附时开合走过与右侧不同的窗口几何，
    /// 现在两侧完全同一套布局与路径。
    /// </summary>
    /// <summary>详情抽屉固定在日历左侧：展开时窗口保持左缘不动、向右变宽，不会带动整窗平移。</summary>
    private void ApplyDrawerColumnWidths(double width)
    {
        var drawer = new GridLength(width);
        var calendar = _calendarWidthDuringDrawerResize is { } widthDuringResize
            ? new GridLength(widthDuringResize)
            : new GridLength(1, GridUnitType.Star);
        LeftColumn.Width = drawer;
        RightColumn.Width = calendar;
    }

    private void ApplyResponsiveMetrics(double scale)
    {
        ApplyDrawerColumnWidths(RightDrawerHost.Visibility == Visibility.Visible ? _savedDrawerLogicalWidth : 0);

        MonthTitle.FontSize = Math.Round(12 * scale, 1);
        MonthTitle.MinWidth = Math.Round(80 * scale);
        SelectedDateText.FontSize = Math.Round(13 * scale, 1);

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

    private Appearance.TintMode CurrentTintMode() =>
        (Appearance.TintMode)Math.Clamp(_settings?.Current.WindowTintMode ?? 0, 0, 3);

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
        UpdateShowTodosVisuals();
        UpdateViewModeVisuals();
    }

    /// <summary>
    /// 两个菜单项都按「勾选 = 显示」表达：待办、已完成待办。
    /// 设置里存的是 CalendarHideCompleted（隐藏完成），这里做一次反向换算。
    /// </summary>
    private void UpdateShowTodosVisuals()
    {
        var showTodos = _settings?.Current.CalendarShowTodos != false;
        var hideCompleted = _settings?.Current.CalendarHideCompleted == true;

        if (ShowTodosMenuItem is not null)
        {
            ShowTodosMenuItem.IsChecked = showTodos;
        }

        if (HideCompletedMenuItem is not null)
        {
            HideCompletedMenuItem.IsChecked = !hideCompleted;
        }

        // 按钮本体只表达「待办是否显示」：显示时用系统强调色（和标题栏其它图标一致），关闭时回到次要色。
        if (CalendarFiltersButton is not null)
        {
            CalendarFiltersButton.Foreground = showTodos
                ? (Brush)Application.Current.Resources["AccentBrush"]
                : (Brush)Application.Current.Resources["TextSecondaryBrush"];
        }
    }

    private void ShowTodos_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null)
        {
            return;
        }

        // ToggleMenuFlyoutItem 点击时已自行翻转，这里以它的状态为准。
        _settings.Current.CalendarShowTodos = ShowTodosMenuItem.IsChecked;
        _settings.Save();
        UpdateShowTodosVisuals();
        Refresh();
    }

    private void HideCompleted_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null)
        {
            return;
        }

        // 勾选表示「显示已完成」，而设置项存的是「隐藏已完成」，需要取反。
        _settings.Current.CalendarHideCompleted = !HideCompletedMenuItem.IsChecked;
        _settings.Save();
        UpdateShowTodosVisuals();
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

        var widthChanged = Math.Abs(Root.ActualWidth - _lastLayoutWidth) >= 4;
        var heightChanged = Math.Abs(Root.ActualHeight - _lastLayoutHeight) >= 4;
        if (!widthChanged && !heightChanged)
        {
            return;
        }

        if (!heightChanged)
        {
            // 只是宽度变了（抽屉开合）：格子随宽度自动重排，不必整块重建，避免白闪。
            _lastLayoutWidth = Root.ActualWidth;
            ApplyResponsiveMetrics(GetUiScale());
            return;
        }

        Refresh();
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

        TraceDrawerToggle("full-refresh");

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

        // 每次重建都从缓存取一次事件：同步回调可能发生在窗口不可见时，
        // 只靠 EventsChanged 推送会漏掉那一轮，导致格子里没有色条。
        LoadCachedEvents();

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
        if (viewModel is null || _settings?.Current.CalendarShowTodos == false)
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

        // 窗口高度不变时行高应保持不变。抽屉开合只改宽度，重建那一帧 DaysGrid 还没布局完，
        // ActualHeight 会短暂为 0，直接用它算会把行高压扁/跳变，所以记住上一次的有效高度。
        var gridHeight = DaysGrid.ActualHeight > 0
            ? DaysGrid.ActualHeight
            : _lastStableDaysHeight > 0
                ? _lastStableDaysHeight
                : Math.Max(MinLogicalHeight, _settings?.Current.CalendarHeight ?? DefaultLogicalHeight) - 104;
        if (DaysGrid.ActualHeight > 0)
        {
            _lastStableDaysHeight = DaysGrid.ActualHeight;
        }

        const double rowSpacing = 4;
        // 行高封顶：窗口拉得很高时一行最多 150，再多就均匀分给各行，
        // 避免单行被拉成一大块空白、色条和条目散在格子里。
        var rowHeight = Math.Clamp(
            Math.Floor((gridHeight - rowSpacing * 5) / 6.0), 46, 150);
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

        // 左右留白与下边距一致（8）；滚动条已隐藏，不再为它预留额外宽度。
        var scrollGutter = 8d;
        var monthGrid = new Grid
        {
            RowSpacing = rowSpacing,
            ColumnSpacing = 4,
            Padding = new Thickness(scrollGutter, 0, scrollGutter, 0),
        };
        for (var column = 0; column < 7; column++)
        {
            monthGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        void AppendWeeks(int startWeekIndex, int weekCount, Dictionary<DateTime, List<CalendarEntry>> datedMap)
        {
            var scale = GetUiScale();
            var headerHeight = Math.Round(21 * scale);
            var laneHeight = Math.Round(15 * scale);
            var laneStride = laneHeight + Math.Round(2 * scale);
            var maxLanes = Math.Clamp((int)Math.Floor((rowHeight - headerHeight - 22 * scale) / laneStride), 0, 3);

            for (var w = 0; w < weekCount; w++)
            {
                var rowIndex = startWeekIndex + w;
                monthGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(rowHeight) });

                var weekStartDate = _monthGridStart.AddDays(rowIndex * 7);
                // 单日的定时日程不以跨日色条呈现，而是和待办一起按时间排进格子。
                var barEvents = _events
                    .Where(evt => evt.IsAllDay ||
                        (evt.EndAt ?? evt.StartAt).Date > evt.StartAt.Date)
                    .ToList();
                var layout = MonthWeekLayout.Build(barEvents, weekStartDate, maxLanes);

                // 每列实际被事件占掉的槽位：没有日程的天不该为别人的色条空出一行。
                var lanesByColumn = new int[7];
                foreach (var weekBar in layout.Bars)
                {
                    for (var c = weekBar.StartColumn; c < weekBar.StartColumn + weekBar.ColumnSpan && c < 7; c++)
                    {
                        lanesByColumn[c] = Math.Max(lanesByColumn[c], weekBar.Lane + 1);
                    }
                }

                var weekGrid = new Grid { ColumnSpacing = 4 };
                for (var col = 0; col < 7; col++)
                {
                    weekGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                }

                for (var col = 0; col < 7; col++)
                {
                    var date = weekStartDate.AddDays(col);
                    var timed = _events
                        .Where(evt => !evt.IsAllDay &&
                            evt.StartAt.Date == date &&
                            (evt.EndAt ?? evt.StartAt).Date == date)
                        .ToList();
                    var cell = BuildCell(date, datedMap.TryGetValue(date, out var items) ? items : null,
                        rowHeight, lanesByColumn[col] * laneStride, timed);
                    Grid.SetColumn(cell, col);
                    weekGrid.Children.Add(cell);
                    _cells.Add((date, cell, true));
                }

                // 叠加层的列必须和 weekGrid 完全一致（含 4px 间距），否则色条会错位。
                var overlay = new Grid { Background = null, ColumnSpacing = weekGrid.ColumnSpacing };
                for (var col = 0; col < 7; col++)
                {
                    overlay.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                }


                overlay.RowDefinitions.Add(new RowDefinition { Height = new GridLength(headerHeight) });
                for (var lane = 0; lane < layout.UsedLanes; lane++)
                {
                    overlay.RowDefinitions.Add(new RowDefinition { Height = new GridLength(laneStride) });
                }

                foreach (var bar in layout.Bars)
                {
                    var barElement = BuildEventBar(bar, laneHeight, scale);
                    Grid.SetRow(barElement, bar.Lane + 1);
                    Grid.SetColumn(barElement, bar.StartColumn);
                    Grid.SetColumnSpan(barElement, bar.ColumnSpan);
                    overlay.Children.Add(barElement);
                }

                Grid.SetRow(overlay, 0);
                Grid.SetColumn(overlay, 0);
                Grid.SetColumnSpan(overlay, 7);
                weekGrid.Children.Add(overlay);

                Grid.SetRow(weekGrid, rowIndex);
                Grid.SetColumn(weekGrid, 0);
                Grid.SetColumnSpan(weekGrid, 7);
                monthGrid.Children.Add(weekGrid);
            }
        }

        AppendWeeks(0, _monthLoadedWeeks, dated);

        var scroll = new ScrollViewer
        {
            // 不显示滚动条：格子和左右边距对称，滚动仍可用（滚轮/触控）。
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
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
            Padding = new Thickness(8, 0, 8, 0),
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

            var dayEvents = _events
                .Where(evt =>
                {
                    var start = evt.StartAt.Date;
                    var end = (evt.EndAt ?? evt.StartAt).Date;
                    if (evt.IsAllDay && end <= start)
                    {
                        end = start;
                    }

                    return date >= start && date <= end;
                })
                .ToList();

            if (entries is { Count: > 0 } || dayEvents.Count > 0)
            {
                var itemsPanel = new StackPanel { Spacing = Math.Round(4 * scale) };
                foreach (var dayEvent in dayEvents)
                {
                    itemsPanel.Children.Add(BuildDayEventCard(dayEvent, scale, showCalendarIcon: true));
                }

                foreach (var entry in entries ?? new List<CalendarEntry>())
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
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(quickAddBtn, "在此日期新建");
            var quickFlyout = new MenuFlyout();
            AppendCreateItems(quickFlyout, date);
            quickAddBtn.Flyout = quickFlyout;
            Grid.SetColumn(quickAddBtn, 2);
            rowGrid.Children.Add(quickAddBtn);

            // 日视图每一行不再套一层卡片：透明容器只负责命中与拖放，
            // 只有选中/今天才给一点底色，其余日子直接贴着背景排。
            var rowCard = new Border
            {
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, Math.Round(7 * scale), 10, Math.Round(7 * scale)),
                Margin = dayOfWeekIdx == 0 ? new Thickness(0, 6, 0, 0) : new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = GetDayRowBackground(isSelected),
                BorderBrush = new SolidColorBrush(Colors.Transparent),
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
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
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

    /// <summary>日视图里的一条日程：左侧日历色竖条 + 标题 + 时间段，单击看详情。</summary>
    private UIElement BuildDayEventCard(CalendarEvent evt, double scale, bool showCalendarIcon = false)
    {
        var color = ParseCalendarColor(evt.Color);
        var padY = Math.Round((showCalendarIcon ? 6 : 5.5) * scale);

        // 侧栏行与待办行共用 20px 图标位 + 8px 间距 + 3px 色条，标题左缘才能严格对齐。
        var spacing = showCalendarIcon ? 8 : Math.Round(8 * scale);
        var grid = new Grid { ColumnSpacing = spacing };
        if (showCalendarIcon)
        {
            // 图标位与待办圆圈同宽（20px），保证两类条目的标题左缘对齐。
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        }

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var column = 0;
        if (showCalendarIcon)
        {
            grid.Children.Add(new FontIcon
            {
                Glyph = "\uE787",
                FontSize = Math.Round(12 * scale, 1),
                Width = 20,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(color),
            });
            column = 1;
        }

        var bar = new Border
        {
            Width = showCalendarIcon ? 3 : Math.Max(3, Math.Round(3 * scale)),
            CornerRadius = new CornerRadius(1.5),
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(color),
        };
        Grid.SetColumn(bar, column);
        grid.Children.Add(bar);

        var title = new TextBlock
        {
            Text = evt.Summary,
            FontSize = Math.Round((showCalendarIcon ? 12 : 11.5) * scale, 1),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
        };
        Grid.SetColumn(title, column + 1);
        grid.Children.Add(title);

        // 时间放右侧，与待办行的 HH:mm 列对齐。
        var time = new TextBlock
        {
            Text = evt.IsAllDay ? "全天" : evt.StartAt.ToString("HH:mm"),
            FontSize = Math.Round(11 * scale, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
        };
        Grid.SetColumn(time, column + 2);
        grid.Children.Add(time);

        var card = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, padY, 8, padY),
            Background = new SolidColorBrush(Color.FromArgb(0x22, color.R, color.G, color.B)),
            CanDrag = true,
        };
        card.Child = grid;
        card.DragStarting += (_, args) =>
        {
            _draggedEntry = null;
            _draggedEvent = evt;
            args.Data.Properties[DragKey] = true;
            args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        };
        card.DropCompleted += (_, _) => _draggedEvent = null;
        ToolTipService.SetToolTip(card, FormatEventTooltip(evt));
        AttachEventContextFlyout(card, evt);
        card.Tapped += async (_, args) =>
        {
            args.Handled = true;
            await ShowEventPreviewAsync(evt);
        };
        return card;
    }

    private UIElement BuildDayEntryCard(CalendarEntry entry, double scale, bool compact = false)
    {        var padY = Math.Round((compact ? 5.5 : 8) * scale);
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
            _draggedEvent = null;
            args.Data.Properties[DragKey] = true;
            args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        };
        row.DropCompleted += (_, _) => _draggedEntry = null;
        AttachEntryContextFlyout(row, entry);

        var content = new Grid { ColumnSpacing = 8 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 勾选圈占满 20px 图标位，与日程行的日历图标同列同宽。
        var check = new CheckBox
        {
            IsChecked = entry.IsDone,
            Style = (Style)Application.Current.Resources["TodoCompletionCheckBoxStyle"],
            Margin = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
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

        // 竖线：与日程行的色条同一列、同一宽度，文字左缘才能对齐。
        var bar = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(1.5),
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(TagColor(entry.TagIndex)),
        };
        Grid.SetColumn(bar, 1);
        content.Children.Add(bar);

        var textStack = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var title = new TextBlock
        {
            Text = entry.Title,
            FontSize = Math.Round(12 * scale, 1),
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

    private Border BuildCell(DateTime date, List<CalendarEntry>? entries, double cellHeight,
        double barsTop = 0, List<CalendarEvent>? timedEvents = null)
    {
        var scale = GetUiScale();
        var inMonth = date.Month == _month.Month;
        var isSelected = date == _selected;
        var isToday = date == DateTime.Today;

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // 每条约 18 高：容量按格子实际高度算，上限随高度放开，
        // 否则窗口放大后格子很空、条目却被折成「+N」。
        var maxChips = Math.Clamp(
            (int)Math.Floor((cellHeight - 24 * scale - barsTop) / (18 * scale)), 0, 40);

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
                // 徽标比普通日期文字高，用负外边距抵消，今天的日期头不会撑高格子内容。
                Margin = new Thickness(0, -Math.Round(1.5 * scale), 0, -Math.Round(1.5 * scale)),
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

        var chipCount = (entries?.Count ?? 0) + (timedEvents?.Count ?? 0);
        if (chipCount > maxChips)
        {
            var moreBadge = new TextBlock
            {
                Text = $"+{chipCount - maxChips}",
                FontSize = Math.Round(9 * scale, 1),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
            };
            Grid.SetColumn(moreBadge, 1);
            dayHeader.Children.Add(moreBadge);
        }

        content.Children.Add(dayHeader);

        if ((entries is { Count: > 0 }) || (timedEvents is { Count: > 0 }))
        {
            var chips = new StackPanel
            {
                Spacing = 2,
                Margin = new Thickness(0, Math.Round(3 * scale) + barsTop, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top,
            };
            Grid.SetRow(chips, 1);

            // 定时日程在上、待办在下，各自按时间排序。
            var shown = 0;
            foreach (var timed in (timedEvents ?? new List<CalendarEvent>()).OrderBy(e => e.StartAt))
            {
                if (shown++ >= maxChips)
                {
                    break;
                }

                chips.Children.Add(BuildEventChip(timed, scale));
            }

            foreach (var entry in entries ?? new List<CalendarEntry>())
            {
                if (shown++ >= maxChips)
                {
                    break;
                }

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

    /// <summary>
    /// 画一条跨日事件色条。首尾是否圆角取决于它在整段里是否真的起点/终点，
    /// 这样跨周时左右会顶到格子边缘，看起来是连续的一条。
    /// </summary>
    private Border BuildEventBar(EventBar bar, double laneHeight, double scale)
    {
        var color = ParseCalendarColor(bar.Event.Color);

        // 苹果原色饱和度高，整块铺满很刺眼。做法与系统日历一致：
        // 底色用低透明度同色，左侧一条实色竖线，文字用近前景色。
        var dark = _settings is null || Appearance.IsDark((ThemeMode)_settings.Current.ThemeMode);
        var fill = Color.FromArgb(dark ? (byte)0x2E : (byte)0x24, color.R, color.G, color.B);
        var stripe = Color.FromArgb(0xFF,
            (byte)(color.R * 0.72 + (dark ? 0 : 40)),
            (byte)(color.G * 0.72 + (dark ? 0 : 40)),
            (byte)(color.B * 0.72 + (dark ? 0 : 40)));

        var text = new TextBlock
        {
            Text = bar.Event.Summary,
            FontSize = Math.Round(9.5 * scale, 1),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
        };

        // 与待办 chip、单日日程 chip 共用同一套左缘度量：
        // 格子内容左缘 → 3px 内边距 → 2.5px 竖线 → 4px 间距 → 正文，三者严格重合。
        var gap = Math.Round(GridEventGap * scale);
        var stripeWidth = Math.Max(2, Math.Round(GridEventStripeWidth * scale));
        var stripeHeight = Math.Max(6, laneHeight - Math.Round(4 * scale));

        var stripeBar = new Border
        {
            Width = stripeWidth,
            Height = stripeHeight,
            CornerRadius = new CornerRadius(stripeWidth / 2),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(color),
        };

        var content = new Grid { ColumnSpacing = gap };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(stripeBar);
        Grid.SetColumn(text, 1);
        content.Children.Add(text);

        var radius = Math.Min(4, Math.Round(laneHeight / 4));
        var pill = new Border
        {
            Height = laneHeight,
            CornerRadius = new CornerRadius(radius),
            Padding = new Thickness(Math.Round(3 * scale), 0, Math.Round(3 * scale), 0),
            // 左缘 6 = 格子内边距 5 + 边框 1，与单日 chip 左缘重合；
            // 在本格结束的色条右缘同样留 6，尾部才能与单日 chip 的圆角右缘对齐。
            Margin = new Thickness(6, 0, bar.RoundedEnd ? 6 : 0, 0),
            Background = new SolidColorBrush(fill),
            Child = content,
        };
        ToolTipService.SetToolTip(pill, FormatEventTooltip(bar.Event));
        AttachEventContextFlyout(pill, bar.Event);

        // 色条本身可点击：单击看详情。
        pill.IsHitTestVisible = true;
        pill.Tapped += async (_, args) =>
        {
            args.Handled = true;
            await ShowEventPreviewAsync(bar.Event);
        };
        return pill;
    }

    /// <summary>日程右键菜单：编辑 / 删除，图标与其它菜单保持一致。</summary>
    private void AttachEventContextFlyout(FrameworkElement host, CalendarEvent evt)
    {
        var flyout = new MenuFlyout();

        var edit = new MenuFlyoutItem
        {
            Text = "编辑",
            Icon = new FontIcon { Glyph = "" },
        };
        edit.Click += (_, _) => _ = EditEventAsync(evt);
        flyout.Items.Add(edit);

        var delete = new MenuFlyoutItem
        {
            Text = "删除",
            Icon = new FontIcon { Glyph = "" },
        };
        delete.Click += (_, _) => _ = DeleteEventAsync(evt);
        flyout.Items.Add(delete);

        host.ContextFlyout = flyout;
    }

    private static string FormatEventTooltip(CalendarEvent evt)
    {
        var header = evt.IsAllDay
            ? evt.StartAt.ToString("M月d日") + "（全天）"
            : evt.StartAt.ToString("M月d日 HH:mm");
        return string.IsNullOrWhiteSpace(evt.Location)
            ? $"{evt.Summary}\n{header}"
            : $"{evt.Summary}\n{header}\n{evt.Location}";
    }

    /// <summary>
    /// 苹果的日历色多数是中等饱和度的亮色（亮黄、亮红），直接压白字看不清。
    /// 按相对亮度决定用深色还是浅色文字，保证在任意日历色上都读得清。
    /// </summary>

    /// <summary>把 #RRGGBBAA（苹果的格式）转成 Color，认不出来就用强调色。</summary>
    private static Color ParseCalendarColor(string value)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                var hex = value.TrimStart('#');
                if (hex.Length == 8)
                {
                    var r = Convert.ToByte(hex[..2], 16);
                    var g = Convert.ToByte(hex.Substring(2, 2), 16);
                    var b = Convert.ToByte(hex.Substring(4, 2), 16);
                    return Color.FromArgb(0xFF, r, g, b);
                }

                if (hex.Length == 6)
                {
                    return Color.FromArgb(0xFF,
                        Convert.ToByte(hex[..2], 16),
                        Convert.ToByte(hex.Substring(2, 2), 16),
                        Convert.ToByte(hex.Substring(4, 2), 16));
                }
            }
        }
        catch
        {
            // 颜色串异常时退回强调色
        }

        return (Application.Current.Resources["AccentBrush"] as SolidColorBrush)?.Color
            ?? Color.FromArgb(0xFF, 0x4D, 0x8F, 0xE3);
    }

    /// <summary>「创建日程 / 创建待办」两项，格子右键菜单和日视图加号共用。</summary>
    private void AppendCreateItems(MenuFlyout flyout, DateTime date)
    {
        var createEvent = new MenuFlyoutItem
        {
            Text = "创建日程",
            Icon = new FontIcon { Glyph = "" },
        };
        createEvent.Click += (_, _) =>
        {
            _selected = date;
            UpdateSelectionVisuals();
            _ = CreateEventAsync(date);
        };
        flyout.Items.Add(createEvent);

        var createTodo = new MenuFlyoutItem
        {
            Text = "创建待办",
            Icon = new FontIcon { Glyph = "" },
        };
        createTodo.Click += (_, _) =>
        {
            _selected = date;
            UpdateSelectionVisuals();
            _createRequested?.Invoke(date);
        };
        flyout.Items.Add(createTodo);

    }

    /// <summary>格子右键菜单：新建两项 + 分隔线 + 打开侧栏。</summary>
    private MenuFlyout BuildCellCreateFlyout(DateTime date)
    {
        var flyout = new MenuFlyout();
        AppendCreateItems(flyout, date);
        flyout.Items.Add(new MenuFlyoutSeparator());

        var openDrawer = new MenuFlyoutItem
        {
            Text = "打开侧栏",
            Icon = new FontIcon { Glyph = "" },
        };
        openDrawer.Click += (_, _) =>
        {
            _selected = date;
            UpdateSelectionVisuals();
            SetRightPanelCollapsed(false);
        };
        flyout.Items.Add(openDrawer);

        return flyout;
    }

    /// <summary>
    /// 日视图选中行的底色。不用整块矩形，改成极淡的强调色微光：
    /// 亮度比周围只高一点点，不会像灰白卡片那样跳出来。
    /// </summary>
    private static Brush GetDayRowBackground(bool selected)
    {
        if (!selected)
        {
            return new SolidColorBrush(Colors.Transparent);
        }

        var dark = Application.Current.Resources["TextPrimaryBrush"] is SolidColorBrush primary &&
            primary.Color.R > 0xC0;
        return new SolidColorBrush(dark
            ? Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x0A, 0x00, 0x00, 0x00));
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

                if (_viewMode == CalendarViewMode.Month &&
                    (tapped.Year != _month.Year || tapped.Month != _month.Month))
                {
                    _month = new DateTime(tapped.Year, tapped.Month, 1);
                    MonthTitle.Text = FormatHeaderTitle();
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(MonthTitle, MonthTitle.Text);
                }

                UpdateSelectionVisuals();
                if (!_rightPanelCollapsed)
                {
                    ShowDetailsForSelected();
                }
            };

            cell.ContextFlyout = BuildCellCreateFlyout(date);

            cell.PointerEntered += (_, _) =>
            {
                if (_viewMode == CalendarViewMode.Day)
                {
                    return;
                }

                cell.Background = (Brush)Application.Current.Resources["HoverBrush"];
            };
            cell.PointerExited += (_, _) =>
            {
                cell.Background = _viewMode == CalendarViewMode.Day
                    ? GetDayRowBackground(date == _selected)
                    : GetCellBackground(date == _selected, IsInCurrentScope(date));
            };
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
            cell.Background = selectable
                ? (_viewMode == CalendarViewMode.Day
                    ? GetDayRowBackground(date == _selected)
                    : GetCellBackground(date == _selected, IsInCurrentScope(date)))
                : normalBrush;

        cell.Drop += async (sender, args) =>
        {
            cell.Background = selectable
                ? (_viewMode == CalendarViewMode.Day
                    ? GetDayRowBackground(date == _selected)
                    : GetCellBackground(date == _selected, IsInCurrentScope(date)))
                : normalBrush;
            if (sender is not FrameworkElement { Tag: DateTime target })
            {
                return;
            }

            args.Handled = true;

            // 单次日程：按原时长整体平移到目标日期，时间点不变。
            if (_draggedEvent is { } draggedEvent)
            {
                _draggedEvent = null;
                if (draggedEvent.StartAt.Date == target.Date)
                {
                    return;
                }

                await MoveEventToDateAsync(draggedEvent, target.Date);
                return;
            }

            if (_draggedEntry is not { } entry || _viewModel is null)
            {
                return;
            }

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

    /// <summary>把单次日程整体挪到目标日期：起止平移相同天数，保留原时刻与时长。</summary>
    private async Task MoveEventToDateAsync(CalendarEvent evt, DateTime targetDate)
    {
        if (_calendarSync is null || _settings is null)
        {
            return;
        }

        var shift = targetDate.Date - evt.StartAt.Date;
        if (shift == TimeSpan.Zero)
        {
            return;
        }

        var draft = new CalendarSyncService.CalendarEventDraft
        {
            Summary = evt.Summary,
            Start = evt.StartAt + shift,
            End = (evt.EndAt ?? evt.StartAt.AddMinutes(30)) + shift,
            AllDay = evt.IsAllDay,
            Location = evt.Location,
            Notes = evt.Notes,
            Url = evt.Url,
            Categories = evt.Categories,
            Busy = evt.IsBusy,
            Class = evt.Class,
            Priority = evt.Priority,
            RRule = evt.RRule,
        };

        try
        {
            if (!string.IsNullOrEmpty(evt.RRule) || evt.RecurrenceId is not null)
            {
                await _calendarSync.UpdateOccurrenceAsync(evt, draft);
            }
            else
            {
                _calendarSync.UpdateEventDeferred(evt, draft);
            }

            LoadCachedEvents();
            Refresh();
        }
        catch (CalDavPendingException pending)
        {
            LoadCachedEvents();
            Refresh();
            UpdateSyncPendingIndicator(pending.Message);
        }
        catch (CalDavException exception)
        {
            await ShowMessageAsync("移动失败", exception.Message);
        }
    }

    private void UpdateSelectionVisuals()
    {
        foreach (var (date, cell, _) in _cells)
        {
            var inScope = IsInCurrentScope(date);
            var isSelected = date == _selected;

            // 日视图的行不是卡片：只有选中态给淡底色，不画边框、不改透明度。
            if (_viewMode == CalendarViewMode.Day)
            {
                cell.Background = GetDayRowBackground(isSelected);
                cell.BorderBrush = new SolidColorBrush(Colors.Transparent);
                cell.Opacity = 1;
                continue;
            }

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

    /// <summary>
    /// 格子里的定时日程：用「淡色填充 + 细圆头色条」的胶囊，和待办的空心圆区分开，
    /// 但仍按时间与待办混排。
    /// </summary>
    private UIElement BuildEventChip(CalendarEvent evt, double scale)
    {
        var color = ParseCalendarColor(evt.Color);
        var dark = _settings is null || Appearance.IsDark((ThemeMode)_settings.Current.ThemeMode);
        var fill = Color.FromArgb(dark ? (byte)0x33 : (byte)0x26, color.R, color.G, color.B);

        var grid = new Grid { ColumnSpacing = Math.Round(GridEventGap * scale) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        grid.Children.Add(new Border
        {
            Width = Math.Max(2, Math.Round(GridEventStripeWidth * scale)),
            Margin = new Thickness(0, Math.Round(1.5 * scale), 0, Math.Round(1.5 * scale)),
            CornerRadius = new CornerRadius(1.5),
            Background = new SolidColorBrush(color),
        });

        var title = new TextBlock
        {
            Text = evt.Summary,
            FontSize = Math.Round(9.5 * scale, 1),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
        };
        SetTitleToolTip(title, FormatEventTooltip(evt));
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);

        var chip = new Border
        {
            CornerRadius = new CornerRadius(4),
            // 左内边距 4 + 色条 + 间距，正好等于待办圆环（色条宽 + 4）占的宽度，
            // 两类条目的文字左缘因此严格重合。
            Padding = new Thickness(4, 1, 3, 1),
            Background = new SolidColorBrush(fill),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CanDrag = true,
            Child = grid,
        };
        chip.DragStarting += (_, args) =>
        {
            _draggedEntry = null;
            _draggedEvent = evt;
            args.Data.Properties[DragKey] = true;
            args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        };
        chip.DropCompleted += (_, _) => _draggedEvent = null;
        chip.Tapped += async (_, args) =>
        {
            args.Handled = true;
            await ShowEventPreviewAsync(evt);
        };
        AttachEventContextFlyout(chip, evt);
        return chip;
    }

    private UIElement BuildChip(CalendarEntry entry, double scale)
    {
        // iOS 日历样式：空心圆 + 文字，没有底色；子任务同样处理，只多缩进一点。
        var tagColor = TagColor(entry.TagIndex);

        var grid = new Grid
        {
            ColumnSpacing = Math.Round(GridEventGap * scale),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 圆环宽度固定取跨日色条槽位，保证三类条目的正文左缘一致。
        var ringSize = Math.Max(6, Math.Round(GridEventStripeWidth * scale) + 4);
        grid.Children.Add(new Border
        {
            Width = ringSize,
            Height = ringSize,
            CornerRadius = new CornerRadius(ringSize / 2),
            BorderThickness = new Thickness(Math.Max(1, Math.Round(1.1 * scale, 1))),
            BorderBrush = new SolidColorBrush(tagColor),
            Background = new SolidColorBrush(Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var title = new TextBlock
        {
            Text = entry.Title,
            FontSize = Math.Round(9.5 * scale, 1),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Left,
            HorizontalAlignment = HorizontalAlignment.Left,
            TextDecorations = entry.IsDone ? Windows.UI.Text.TextDecorations.Strikethrough : Windows.UI.Text.TextDecorations.None,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
        };
        SetTitleToolTip(title, entry.FullTitle);
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);

        var chip = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(0, 1, 2, 1),
            Background = new SolidColorBrush(Colors.Transparent),
            Opacity = entry.IsDone ? 0.55 : 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CanDrag = true,
            Child = grid,
        };
        chip.DragStarting += (_, args) =>
        {
            _draggedEntry = entry;
            _draggedEvent = null;
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

        var toggleDone = new MenuFlyoutItem
        {
            Text = entry.IsDone ? "标记为未完成" : "标记为已完成",
            Icon = new FontIcon { Glyph = "" },
        };
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

        var clearDue = new MenuFlyoutItem
        {
            Text = "清除时间",
            Icon = new FontIcon { Glyph = "" },
        };
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

        var edit = new MenuFlyoutItem
        {
            Text = "编辑",
            Icon = new FontIcon { Glyph = "" },
        };
        edit.Click += (_, _) => _editRequested?.Invoke(entry.Parent);
        flyout.Items.Add(edit);

        var delete = new MenuFlyoutItem
        {
            Text = "删除",
            Icon = new FontIcon { Glyph = "" },
        };
        delete.Click += async (_, _) => await DeleteCalendarEntryAsync(entry);
        flyout.Items.Add(delete);

        host.ContextFlyout = flyout;
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
        dialog.Resources["ContentDialogBackground"] = ThemePalette.CreateDialogAcrylicBrush(dark, CurrentTintMode());
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

    /// <summary>某天覆盖到的日程（跨日与全天按区间判断）。</summary>
    private IEnumerable<CalendarEvent> EventsForDate(DateTime date) =>
        _events.Where(evt =>
        {
            var start = evt.StartAt.Date;
            var end = (evt.EndAt ?? evt.StartAt).Date;
            if (evt.IsAllDay && end <= start)
            {
                end = start;
            }

            return date >= start && date <= end;
        });

    private void ShowSelectedDay(List<CalendarEntry> entries)
    {
        SelectedDateText.Text = _selected.ToString("M 月 d 日 dddd");
        DetailsPanel.Children.Clear();

        var dayEvents = EventsForDate(_selected).ToList();
        if (entries.Count == 0 && dayEvents.Count == 0)
        {
            DetailsPanel.Children.Add(new TextBlock
            {
                Text = "这天没有安排",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
            });
            return;
        }

        var scale = GetUiScale();

        // 日程在上、待办在下，各自按时间排序。
        foreach (var dayEvent in dayEvents.OrderBy(e => e.StartAt))
        {
            DetailsPanel.Children.Add(BuildDayEventCard(dayEvent, scale, showCalendarIcon: true));
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
            _draggedEvent = null;
            args.Data.Properties[DragKey] = true;
            args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        };
        row.DropCompleted += (_, _) => _draggedEntry = null;

        var content = new Grid { ColumnSpacing = 8 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 与主界面待办行一致：可直接点选的圆圈 + 标签色竖线。
        var check = new CheckBox
        {
            IsChecked = entry.IsDone,
            Style = (Style)Application.Current.Resources["TodoCompletionCheckBoxStyle"],
            Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(check, "标记完成");
        check.Tapped += (_, args) => args.Handled = true;
        check.Click += (_, _) =>
        {
            if (_viewModel is null)
            {
                return;
            }

            var done = check.IsChecked == true;
            if (entry.Subtask is { } subtask)
            {
                _viewModel.SetSubtaskDone(entry.Parent, subtask, done);
            }
            else
            {
                _viewModel.SetDone(entry.Parent, done);
            }

            Refresh();
        };
        content.Children.Add(check);

        var bar = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(1.5),
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(TagColor(entry.TagIndex)),
        };
        Grid.SetColumn(bar, 1);
        content.Children.Add(bar);

        var title = new TextBlock
        {
            Text = entry.Title,
            FontSize = Math.Round(12 * scale, 1),
            MaxLines = 2,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources[
                entry.IsDone ? "TextMutedBrush" : "TextPrimaryBrush"],
            TextDecorations = entry.IsDone
                ? Windows.UI.Text.TextDecorations.Strikethrough
                : Windows.UI.Text.TextDecorations.None,
        };
        SetTitleToolTip(title, entry.FullTitle);
        Grid.SetColumn(title, 2);
        content.Children.Add(title);

        var time = new TextBlock
        {
            Text = entry.DueAt?.ToString("HH:mm") ?? string.Empty,
            FontSize = Math.Round(11 * scale, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources[
                entry.IsOverdue ? "OverdueBrush" : "TextMutedBrush"],
        };
        Grid.SetColumn(time, 3);
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
            // 缩进表示层级：从主任务文字左缘起排。
            Padding = new Thickness(Math.Round(34 * scale), 2, 6, 2),
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
            _draggedEvent = null;
            args.Data.Properties[DragKey] = true;
            args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        };
        row.DropCompleted += (_, _) => _draggedEntry = null;

        var content = new Grid { ColumnSpacing = 8 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 子任务同样用可点的圆圈 + 细一点的标签色竖线，只靠缩进区分层级。
        var check = new CheckBox
        {
            IsChecked = subtask.IsDone,
            Style = (Style)Application.Current.Resources["TodoCompletionCheckBoxStyle"],
            Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(check, "标记完成");
        check.Tapped += (_, args) => args.Handled = true;
        check.Click += (_, _) =>
        {
            if (_viewModel is null)
            {
                return;
            }

            _viewModel.SetSubtaskDone(parent, subtask, check.IsChecked == true);
            Refresh();
        };
        content.Children.Add(check);

        var bar = new Border
        {
            Width = 2,
            CornerRadius = new CornerRadius(1),
            VerticalAlignment = VerticalAlignment.Stretch,
            Opacity = 0.7,
            Background = new SolidColorBrush(TagColor(parent.TagIndex)),
        };
        Grid.SetColumn(bar, 1);
        content.Children.Add(bar);

        var title = new TextBlock
        {
            Text = subtask.Title,
            FontSize = Math.Round(11 * scale, 1),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources[
                subtask.IsDone ? "TextMutedBrush" : "TextSecondaryBrush"],
            TextDecorations = subtask.IsDone
                ? Windows.UI.Text.TextDecorations.Strikethrough
                : Windows.UI.Text.TextDecorations.None,
        };
        SetTitleToolTip(title, entry.FullTitle);
        Grid.SetColumn(title, 2);
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
            Grid.SetColumn(time, 3);
            content.Children.Add(time);
        }

        row.Children.Add(content);
        return row;
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

            var scale = GetUiScale();
            var primary = (Brush)Application.Current.Resources["TextPrimaryBrush"];
            var secondary = (Brush)Application.Current.Resources["TextSecondaryBrush"];
            var muted = (Brush)Application.Current.Resources["TextMutedBrush"];
            var overdue = (Brush)Application.Current.Resources["OverdueBrush"];
            var cardFill = (Brush)Application.Current.Resources["ItemCardBrush"];
            var cardStroke = (Brush)Application.Current.Resources["ItemCardStrokeBrush"];
            var tagColor = TagColor(entry.TagIndex);

            Border Card(UIElement child) => new()
            {
                Padding = new Thickness(14, 12, 14, 12),
                Background = cardFill,
                BorderBrush = cardStroke,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Child = child,
            };

            TextBlock OptionRow(Panel host, string glyph, string label, string value)
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Math.Round(6 * scale) };
                line.Children.Add(new FontIcon
                {
                    Glyph = glyph,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = muted,
                });
                var text = new TextBlock
                {
                    Text = value,
                    FontSize = Math.Round(12.5 * scale, 1),
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = primary,
                    IsTextSelectionEnabled = true,
                };
                line.Children.Add(text);

                var row = new StackPanel { Spacing = Math.Round(3 * scale) };
                row.Children.Add(new TextBlock
                {
                    Text = label,
                    FontSize = Math.Round(10.5 * scale, 1),
                    Foreground = muted,
                });
                row.Children.Add(line);
                host.Children.Add(row);
                return text;
            }

            var panel = new StackPanel { Spacing = 10, MinWidth = Math.Round(330 * scale) };

            // 主卡片：标签颜色竖条 + 完成勾 + 标题 + 时间/状态，与日程详情同一版式。
            var headerGrid = new Grid { ColumnSpacing = Math.Round(10 * scale) };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            headerGrid.Children.Add(new Border
            {
                Width = 4,
                CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = new SolidColorBrush(tagColor),
            });

            var completionBox = new CheckBox
            {
                IsChecked = entry.IsDone,
                Style = (Style)Application.Current.Resources["TodoCompletionCheckBoxStyle"],
                Margin = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(completionBox, "标记完成");
            Grid.SetColumn(completionBox, 1);
            headerGrid.Children.Add(completionBox);

            var headerText = new StackPanel { Spacing = Math.Round(6 * scale) };
            headerText.Children.Add(new TextBlock
            {
                Text = entry.Title,
                FontSize = Math.Round(16 * scale, 1),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Foreground = entry.IsDone ? muted : primary,
                TextDecorations = entry.IsDone
                    ? Windows.UI.Text.TextDecorations.Strikethrough
                    : Windows.UI.Text.TextDecorations.None,
                IsTextSelectionEnabled = true,
            });
            var title = (TextBlock)headerText.Children[0];

            if (entry.Subtask is not null)
            {
                OptionRow(headerText, "\uE73A", "主任务", entry.Parent.Title).Foreground = secondary;
            }

            var due = entry.DueAt;
            var timeValueText = OptionRow(
                headerText,
                "\uE823",
                "时间",
                due is { } dueValue ? dueValue.ToString("yyyy/M/d HH:mm") : "无截止时间");
            timeValueText.Foreground = due is not null && entry.IsOverdue ? overdue : secondary;

            var statusValueText = OptionRow(
                headerText,
                "\uE930",
                "状态",
                entry.IsDone ? "已完成" : entry.IsOverdue ? "已逾期" : "未完成");
            statusValueText.Foreground = entry.IsOverdue ? overdue : secondary;

            Grid.SetColumn(headerText, 2);
            headerGrid.Children.Add(headerText);
            panel.Children.Add(Card(headerGrid));

            // 选项卡片：标签 / 重复 / 重要。
            var options = new StackPanel { Spacing = Math.Round(12 * scale) };
            if (entry.TagIndex >= 0)
            {
                OptionRow(options, "\uE8EC", "标签", settings.GetTagName(entry.TagIndex));
            }

            if (entry.Subtask is null)
            {
                var parent = entry.Parent;
                if (parent.Recurrence != TodoRecurrence.None)
                {
                    OptionRow(options, "\uE895", "重复", parent.RecurrenceText);
                }

                if (parent.IsImportant)
                {
                    OptionRow(options, "\uE734", "标记", "重要");
                }
            }

            if (options.Children.Count > 0)
            {
                panel.Children.Add(Card(options));
            }

            // 备注卡片：子任务详情里先列自己的备注，再列主任务的备注。
            var subtaskNote = entry.Subtask?.Note ?? string.Empty;
            var parentNote = entry.Parent.Note;
            if (!string.IsNullOrWhiteSpace(subtaskNote) || !string.IsNullOrWhiteSpace(parentNote))
            {
                var notes = new StackPanel { Spacing = Math.Round(12 * scale) };
                if (!string.IsNullOrWhiteSpace(subtaskNote))
                {
                    OptionRow(notes, "", "备注", subtaskNote);
                }

                if (!string.IsNullOrWhiteSpace(parentNote))
                {
                    OptionRow(notes, "", entry.Subtask is not null ? "主任务备注" : "备注", parentNote);
                }

                panel.Children.Add(Card(notes));
            }

            // 子任务卡片（仅主任务）。
            TextBlock? subtaskProgressText = null;
            var subtaskRows = new List<(SubtaskItem Subtask, CheckBox Check, TextBlock Title, TextBlock Time)>();
            if (entry.Subtask is null && entry.Parent.Subtasks.Count > 0)
            {
                var subHeader = new Grid { ColumnSpacing = Math.Round(8 * scale) };
                subHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                subHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                subHeader.Children.Add(new TextBlock
                {
                    Text = "子任务",
                    FontSize = Math.Round(10.5 * scale, 1),
                    Foreground = muted,
                });
                subtaskProgressText = new TextBlock
                {
                    Text = entry.Parent.SubtaskProgressText,
                    FontSize = Math.Round(10.5 * scale, 1),
                    Foreground = muted,
                };
                Grid.SetColumn(subtaskProgressText, 1);
                subHeader.Children.Add(subtaskProgressText);

                var subList = new StackPanel { Spacing = Math.Round(8 * scale) };
                subList.Children.Add(subHeader);
                foreach (var subtask in entry.Parent.Subtasks)
                {
                    var (row, check, subTitle, subTime) = BuildSubtaskPreviewRow(entry.Parent, subtask, () =>
                    {
                        SyncPreviewVisuals();
                        Refresh();
                    });
                    subtaskRows.Add((subtask, check, subTitle, subTime));
                    subList.Children.Add(row);
                }

                panel.Children.Add(Card(subList));
            }

            void SyncPreviewVisuals()
            {
                var isDone = entry.Subtask is { } s ? s.IsDone : entry.Parent.IsDone;
                completionBox.IsChecked = isDone;
                title.Foreground = isDone ? muted : primary;
                title.TextDecorations = isDone
                    ? Windows.UI.Text.TextDecorations.Strikethrough
                    : Windows.UI.Text.TextDecorations.None;

                var isOverdue = entry.Subtask is { } s2 ? s2.IsOverdue : entry.Parent.IsOverdue;
                timeValueText.Foreground = entry.DueAt is not null && isOverdue ? overdue : secondary;
                statusValueText.Text = isDone ? "已完成" : isOverdue ? "已逾期" : "未完成";
                statusValueText.Foreground = isOverdue ? overdue : secondary;

                if (subtaskProgressText is not null)
                {
                    subtaskProgressText.Text = entry.Parent.SubtaskProgressText;
                }

                foreach (var (subtask, subCheck, subTitle, subTime) in subtaskRows)
                {
                    subCheck.IsChecked = subtask.IsDone;
                    subTitle.Foreground = subtask.IsDone ? muted : primary;
                    subTitle.TextDecorations = subtask.IsDone
                        ? Windows.UI.Text.TextDecorations.Strikethrough
                        : Windows.UI.Text.TextDecorations.None;
                    subTime.Foreground = subtask.IsOverdue ? overdue : muted;
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

            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = entry.Subtask is null ? "任务详情" : "子任务详情",
                Content = new ScrollViewer
                {
                    Content = panel,
                    MinWidth = Math.Round(350 * scale),
                    MaxHeight = 430,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Padding = new Thickness(0, 0, 8, 0),
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
            dialog.Resources["ContentDialogBackground"] = ThemePalette.CreateDialogAcrylicBrush(dark, CurrentTintMode());
            dialog.Resources["ContentDialogBorderBrush"] = Application.Current.Resources["CardStrokeBrush"];
            dialog.Resources["ContentDialogCornerRadius"] = new CornerRadius(12);
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

    /// <summary>把 RRULE 翻译成一句人话，认不出的组合就退回原串。</summary>
    private static string DescribeRRule(string rrule)
    {
        var rule = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in rrule.Split(';'))
        {
            var eq = part.IndexOf('=');
            if (eq > 0)
            {
                rule[part[..eq].Trim().ToUpperInvariant()] = part[(eq + 1)..].Trim();
            }
        }

        if (!rule.TryGetValue("FREQ", out var freq))
        {
            return rrule;
        }

        var interval = rule.TryGetValue("INTERVAL", out var intervalText) && int.TryParse(intervalText, out var parsed)
            ? Math.Max(1, parsed)
            : 1;

        var head = freq.ToUpperInvariant() switch
        {
            "DAILY" => interval == 1 ? "每天" : $"每 {interval} 天",
            "WEEKLY" => interval == 1 ? "每周" : $"每 {interval} 周",
            "MONTHLY" => interval == 1 ? "每月" : $"每 {interval} 个月",
            "YEARLY" => interval == 1 ? "每年" : $"每 {interval} 年",
            _ => null,
        };
        if (head is null)
        {
            return rrule;
        }

        var parts = new List<string> { head };

        if (rule.TryGetValue("BYDAY", out var byDay) && !string.IsNullOrWhiteSpace(byDay))
        {
            var names = byDay.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(token => token.Trim().ToUpperInvariant() switch
                {
                    "MO" => "周一",
                    "TU" => "周二",
                    "WE" => "周三",
                    "TH" => "周四",
                    "FR" => "周五",
                    "SA" => "周六",
                    "SU" => "周日",
                    _ => null,
                })
                .Where(name => name is not null)
                .ToList();
            if (names.Count > 0)
            {
                parts.Add("（" + string.Join("、", names) + "）");
            }
        }

        if (rule.TryGetValue("BYMONTHDAY", out var byMonthDay) && !string.IsNullOrWhiteSpace(byMonthDay))
        {
            parts.Add("（" + byMonthDay.Replace(",", "、") + " 日）");
        }

        if (rule.TryGetValue("COUNT", out var count))
        {
            parts.Add($"，共 {count} 次");
        }
        else if (rule.TryGetValue("UNTIL", out var until))
        {
            if (IcsCalendar.ParseStamp(until) is { } untilDate)
            {
                parts.Add($"，至 {untilDate:yyyy/M/d}");
            }
        }

        return string.Concat(parts);
    }


    private async Task ShowEventPreviewAsync(CalendarEvent evt)
    {
        if (_previewOpen)
        {
            return;
        }

        var settings = _settings;
        _previewOpen = true;
        try
        {
            if (settings is null)
            {
                return;
            }

            var scale = GetUiScale();
            var primary = (Brush)Application.Current.Resources["TextPrimaryBrush"];
            var secondary = (Brush)Application.Current.Resources["TextSecondaryBrush"];
            var muted = (Brush)Application.Current.Resources["TextMutedBrush"];
            var accent = (Brush)Application.Current.Resources["AccentBrush"];
            var cardFill = (Brush)Application.Current.Resources["ItemCardBrush"];
            var cardStroke = (Brush)Application.Current.Resources["ItemCardStrokeBrush"];
            var color = ParseCalendarColor(evt.Color);

            Border Card(UIElement child, Thickness? padding = null) => new()
            {
                Padding = padding ?? new Thickness(14, 12, 14, 12),
                Background = cardFill,
                BorderBrush = cardStroke,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Child = child,
            };

            // 一行「图标 + 标签 + 值」，返回值是值文本，便于后续改色/加点击。
            TextBlock OptionRow(Panel host, string glyph, string label, string value)
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Math.Round(6 * scale) };
                line.Children.Add(new FontIcon
                {
                    Glyph = glyph,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = muted,
                });
                var text = new TextBlock
                {
                    Text = value,
                    FontSize = Math.Round(12.5 * scale, 1),
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = primary,
                    IsTextSelectionEnabled = true,
                };
                line.Children.Add(text);

                var row = new StackPanel { Spacing = Math.Round(3 * scale) };
                row.Children.Add(new TextBlock
                {
                    Text = label,
                    FontSize = Math.Round(10.5 * scale, 1),
                    Foreground = muted,
                });
                row.Children.Add(line);
                host.Children.Add(row);
                return text;
            }

            var panel = new StackPanel { Spacing = 10, MinWidth = Math.Round(330 * scale) };

            // 主卡片：颜色条 + 标题 + 时间 + 所属日历。
            var headerGrid = new Grid { ColumnSpacing = Math.Round(10 * scale) };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.Children.Add(new Border
            {
                Width = 4,
                CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = new SolidColorBrush(color),
            });

            var headerText = new StackPanel { Spacing = Math.Round(6 * scale) };
            headerText.Children.Add(new TextBlock
            {
                Text = evt.Summary,
                FontSize = Math.Round(16 * scale, 1),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Foreground = primary,
                IsTextSelectionEnabled = true,
            });

            var timeText = evt.IsAllDay
                ? $"{evt.StartAt:yyyy/M/d}（全天）"
                : evt.EndAt is { } end && end.Date == evt.StartAt.Date
                    ? $"{evt.StartAt:yyyy/M/d HH:mm}–{end:HH:mm}"
                    : evt.EndAt is { } endMulti
                        ? $"{evt.StartAt:yyyy/M/d HH:mm} – {endMulti:yyyy/M/d HH:mm}"
                        : $"{evt.StartAt:yyyy/M/d HH:mm}";
            OptionRow(headerText, "\uE823", "时间", timeText).Foreground = secondary;

            var sourceName = _calendarSync?.Repository.GetSources()
                .FirstOrDefault(s => s.Href == evt.CalendarHref)?.DisplayName;
            if (!string.IsNullOrWhiteSpace(sourceName))
            {
                OptionRow(headerText, "\uE787", "日历", sourceName).Foreground = secondary;
            }

            Grid.SetColumn(headerText, 1);
            headerGrid.Children.Add(headerText);
            panel.Children.Add(Card(headerGrid));

            // 选项卡片。
            var options = new StackPanel { Spacing = Math.Round(12 * scale) };
            if (!string.IsNullOrWhiteSpace(evt.RRule))
            {
                OptionRow(options, "\uE895", "重复", DescribeRRule(evt.RRule));
            }

            if (!string.IsNullOrWhiteSpace(evt.Location))
            {
                OptionRow(options, "\uE707", "地点", evt.Location);
            }


            if (!string.IsNullOrEmpty(evt.Class) && !string.Equals(evt.Class, "PUBLIC", StringComparison.OrdinalIgnoreCase))
            {
                OptionRow(options, "\uE72E", "隐私",
                    string.Equals(evt.Class, "PRIVATE", StringComparison.OrdinalIgnoreCase) ? "私密" : "机密");
            }

            if (evt.Priority > 0)
            {
                OptionRow(options, "\uE7C1", "优先级", evt.Priority switch { <= 4 => "高", 5 => "中", _ => "低" });
            }

            if (!string.IsNullOrEmpty(evt.Status) &&
                !string.Equals(evt.Status, "CONFIRMED", StringComparison.OrdinalIgnoreCase))
            {
                OptionRow(options, "\uE8B7", "状态",
                    string.Equals(evt.Status, "TENTATIVE", StringComparison.OrdinalIgnoreCase) ? "待定" : "已取消");
            }

            if (options.Children.Count > 0)
            {
                panel.Children.Add(Card(options));
            }

            // 详细信息卡片：备注 / 链接 / 分类。
            var details = new StackPanel { Spacing = Math.Round(12 * scale) };
            if (!string.IsNullOrWhiteSpace(evt.Notes))
            {
                OptionRow(details, "\uE70B", "备注", evt.Notes);
            }

            if (!string.IsNullOrWhiteSpace(evt.Url))
            {
                var linkText = OptionRow(details, "\uE71B", "链接", evt.Url);
                linkText.Foreground = accent;
                var capturedUrl = evt.Url;
                linkText.Tapped += async (_, _) =>
                {
                    try
                    {
                        if (Uri.TryCreate(capturedUrl, UriKind.Absolute, out var uri))
                        {
                            await Windows.System.Launcher.LaunchUriAsync(uri);
                        }
                    }
                    catch
                    {
                        // 打不开链接就忽略
                    }
                };
            }

            if (!string.IsNullOrWhiteSpace(evt.Categories))
            {
                OptionRow(details, "\uE8EC", "分类", evt.Categories);
            }

            if (details.Children.Count > 0)
            {
                panel.Children.Add(Card(details));
            }

            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "日程详情",
                Content = new ScrollViewer
                {
                    Content = panel,
                    MinWidth = Math.Round(350 * scale),
                    MaxHeight = 430,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Padding = new Thickness(0, 0, 8, 0),
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
            dialog.Resources["ContentDialogBackground"] = ThemePalette.CreateDialogAcrylicBrush(dark, CurrentTintMode());
            dialog.Resources["ContentDialogBorderBrush"] = Application.Current.Resources["CardStrokeBrush"];
            dialog.Resources["ContentDialogCornerRadius"] = new CornerRadius(12);
            dialog.Resources["ContentDialogSmokeFill"] = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x00, 0x00));

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                // 等详情关掉再开编辑窗，避免两个弹层抢焦点。
                DispatcherQueue.TryEnqueue(() => _ = EditEventAsync(evt));
            }
        }
        finally
        {
            _previewOpen = false;
        }
    }

    /// <summary>编辑日程；循环日程先问范围，再决定改这一次还是整个系列。</summary>
    private async Task EditEventAsync(CalendarEvent evt)
    {
        if (_calendarSync is null || _settings is null)
        {
            return;
        }

        var isRecurring = !string.IsNullOrEmpty(evt.RRule) || evt.RecurrenceId is not null;
        var editSeries = false;

        if (isRecurring)
        {
            var scope = await ChooseRecurringScopeAsync(
                "编辑重复日程",
                $"「{evt.Summary}」是重复日程，想改哪一部分？",
                primaryText: "仅这一次",
                secondaryText: "整个系列");
            if (scope is null)
            {
                return;
            }

            editSeries = scope == RecurringScope.Series;
        }

        // 整个系列改为编辑母事件；仅这一次写 RECURRENCE-ID 覆盖实例。
        var target = evt;
        if (editSeries)
        {
            target = _calendarSync.GetSeriesMaster(evt) ?? evt;
        }

        var dialog = new CalendarEventDialog(
            _settings, _calendarSync, target.StartAt.Date, target.CalendarHref, target,
            editSeries ? string.Empty : isRecurring ? "（仅这一次）" : string.Empty,
            allowRepeatEdit: !isRecurring || editSeries,
            lockCalendar: isRecurring && !editSeries);
        dialog.ShowOver(null, topmost: IsHostTopmost?.Invoke() == true);
        if (!await dialog.Result || dialog.Draft is not { } draft)
        {
            return;
        }

        // 换了日历：CalDAV 不能直接移动，走「新集合新建 + 删旧资源」。
        var calendarChanged = dialog.TargetCalendarHref is { } href &&
            !string.Equals(href, target.CalendarHref, StringComparison.OrdinalIgnoreCase);

        try
        {
            if (calendarChanged)
            {
                await _calendarSync.MoveEventToCalendarAsync(target, dialog.TargetCalendarHref!, draft);
            }
            else if (editSeries)
            {
                await _calendarSync.UpdateSeriesAsync(target, draft);
            }
            else if (isRecurring)
            {
                await _calendarSync.UpdateOccurrenceAsync(evt, draft);
            }
            else
            {
                await _calendarSync.UpdateEventAsync(evt, draft);
            }

            LoadCachedEvents();
            Refresh();
        }
        catch (CalDavPendingException pending)
        {
            LoadCachedEvents();
            Refresh();
            UpdateSyncPendingIndicator(pending.Message);
        }
        catch (CalDavException exception)
        {
            await ShowMessageAsync("保存失败", exception.Message);
        }
    }

    private enum RecurringScope
    {
        Occurrence,
        Series,
    }

    /// <summary>循环日程的作用范围选择：主按钮这一次，次按钮整个系列。</summary>
    private async Task<RecurringScope?> ChooseRecurringScopeAsync(
        string title, string message, string primaryText, string secondaryText)
    {
        var dialog = CreateEntryDialog(title, message, primaryText);
        dialog.SecondaryButtonText = secondaryText;
        dialog.DefaultButton = ContentDialogButton.Primary;

        var result = await dialog.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary => RecurringScope.Occurrence,
            ContentDialogResult.Secondary => RecurringScope.Series,
            _ => null,
        };
    }

    private async Task DeleteEventAsync(CalendarEvent evt, bool confirm = true)
    {
        if (_calendarSync is null)
        {
            return;
        }

        var isRecurring = !string.IsNullOrEmpty(evt.RRule) || evt.RecurrenceId is not null;
        var deleteSeries = false;

        if (confirm)
        {
            if (isRecurring)
            {
                var scope = await ChooseRecurringScopeAsync(
                    "删除重复日程",
                    $"「{evt.Summary}」是重复日程，删除 {evt.StartAt:M 月 d 日} 这一次还是整个系列？",
                    primaryText: "仅这一次",
                    secondaryText: "整个系列");
                if (scope is null)
                {
                    return;
                }

                deleteSeries = scope == RecurringScope.Series;
            }
            else
            {
                var dialog = CreateEntryDialog(
                    "删除日程",
                    $"确定删除「{evt.Summary}」吗？",
                    "删除");
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }
            }
        }

        try
        {
            if (isRecurring && !deleteSeries)
            {
                await _calendarSync.DeleteOccurrenceAsync(evt);
            }
            else
            {
                await _calendarSync.DeleteEventAsync(evt);
            }

            LoadCachedEvents();
            Refresh();
        }
        catch (CalDavPendingException pending)
        {
            LoadCachedEvents();
            Refresh();
            UpdateSyncPendingIndicator(pending.Message);
        }
        catch (CalDavException exception)
        {
            await ShowMessageAsync("删除失败", exception.Message);
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        if (_settings is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "知道了",
        };
        dialog.RequestedTheme = Root.RequestedTheme == ElementTheme.Default
            ? Appearance.ToElementTheme((ThemeMode)_settings.Current.ThemeMode)
            : Root.RequestedTheme;
        var dark = dialog.RequestedTheme == ElementTheme.Dark;
        ThemePalette.ApplyTextControlPalette(dialog.Resources);
        dialog.Resources["ContentDialogBackground"] = ThemePalette.CreateDialogAcrylicBrush(dark, CurrentTintMode());
        dialog.Resources["ContentDialogBorderBrush"] = Application.Current.Resources["CardStrokeBrush"];
        dialog.Resources["ContentDialogCornerRadius"] = new CornerRadius(8);
        dialog.Resources["ContentDialogSmokeFill"] = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x00, 0x00));
        await dialog.ShowAsync();
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

    private void NewTask_Click(object sender, RoutedEventArgs e)
    {
        // 侧栏的加号同样给两个选择，避免只能建待办。
        var flyout = new MenuFlyout();
        AppendCreateItems(flyout, _selected);
        flyout.ShowAt((FrameworkElement)sender, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight,
        });
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
        StopMonthNavDrag();
        _draggedEntry = null;
        _draggedEvent = null;
    }

    private long _drawerTraceStarted;
    private string? _lastDrawerLayout;

    internal void TraceDrawerToggle(string stage)
    {
        try
        {
            var path = System.IO.Path.Combine(AppPaths.Root, "calendar-drawer.log");
            if (stage.StartsWith("begin-", StringComparison.Ordinal))
            {
                if (new System.IO.FileInfo(path) is { Exists: true, Length: > 65536 })
                {
                    System.IO.File.WriteAllText(path, string.Empty);
                }

                _drawerTraceStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                _lastDrawerLayout = null;
            }

            if (_drawerTraceStarted == 0 ||
                System.Diagnostics.Stopwatch.GetElapsedTime(_drawerTraceStarted).TotalMilliseconds > 700)
            {
                return;
            }

            var origin = CalendarSurface.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point());
            var layout = $"collapsed={_rightPanelCollapsed} " +
                $"root={Root.ActualWidth:F1}x{Root.ActualHeight:F1} cols={LeftColumn.ActualWidth:F1}/{RightColumn.ActualWidth:F1} " +
                $"surface={origin.X:F1},{origin.Y:F1},{CalendarSurface.ActualWidth:F1}x{CalendarSurface.ActualHeight:F1} " +
                $"drawer={RightDrawerHost.ActualWidth:F1} scroll={_activeScroll?.VerticalOffset:F1}";
            if (stage == "layout" && layout == _lastDrawerLayout)
            {
                return;
            }

            _lastDrawerLayout = layout;
            System.IO.File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {stage} {layout}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
        }
    }

    internal void AwaitDrawerResize(double expectedWidth)
    {
        _pendingDrawerResizeWidth = expectedWidth;
        if (Math.Abs(Root.ActualWidth - expectedWidth) < 1)
        {
            FinishDrawerResize();
            return;
        }

        if (_drawerResizeFallback is null)
        {
            _drawerResizeFallback = DispatcherQueue.CreateTimer();
            _drawerResizeFallback.IsRepeating = false;
            _drawerResizeFallback.Interval = TimeSpan.FromMilliseconds(250);
            _drawerResizeFallback.Tick += (_, _) => FinishDrawerResize();
        }

        _drawerResizeFallback.Start();
    }

    private void FinishDrawerResize()
    {
        _drawerResizeFallback?.Stop();
        _pendingDrawerResizeWidth = null;
        _calendarWidthDuringDrawerResize = null;
        ApplyResponsiveMetrics(GetUiScale());
        TraceDrawerToggle("resize-settled");
    }

    private void RightPanelToggle_Click(object sender, RoutedEventArgs e) =>
        SetRightPanelCollapsed(!_rightPanelCollapsed);

    private void SetRightPanelCollapsed(bool collapsed, bool refresh = true, bool notifyHost = true)
    {
        if (_rightPanelCollapsed == collapsed || _calendarWidthDuringDrawerResize is not null)
        {
            return;
        }

        // Win32 bounds arrive before WinUI's next arrange; keep the calendar column stable across that gap.
        var calendarColumn = RightColumn;
        _calendarWidthDuringDrawerResize = calendarColumn.ActualWidth > 0 ? calendarColumn.ActualWidth : null;

        _rightPanelCollapsed = collapsed;
        TraceDrawerToggle(collapsed ? "begin-collapse" : "begin-expand");
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
            var drawerWidth = LeftColumn.Width.Value;
            _savedDrawerLogicalWidth = drawerWidth > 0
                ? drawerWidth
                : Math.Round(Math.Clamp(fallbackTotal * 0.31, 210, 380)) + 10;

            var ease = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.4f, 0f),
                new Vector2(1f, 1f));
            var slideAnim = compositor.CreateVector3KeyFrameAnimation();
            slideAnim.Duration = TimeSpan.FromMilliseconds(140);
            slideAnim.InsertKeyFrame(0f, Vector3.Zero);
            slideAnim.InsertKeyFrame(1f, new Vector3(-36f, 0f, 0f), ease);

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

                ApplyDrawerColumnWidths(0);
                TraceDrawerToggle("collapse-animation-complete");
                RightDrawerHost.Visibility = Visibility.Collapsed;
                drawerVisual.Properties.InsertVector3("Translation", Vector3.Zero);
                drawerVisual.Opacity = 1f;
                if (notifyHost)
                {
                    RightPanelCollapsedChanged?.Invoke(this, true);
                }
            });

            drawerVisual.StartAnimation("Translation", slideAnim);
            drawerVisual.StartAnimation("Opacity", fadeAnim);
            batch.End();
            return;
        }

        drawerVisual.Opacity = 0f;
        var startOffset = new Vector3(-36f, 0f, 0f);
        drawerVisual.Properties.InsertVector3("Translation", startOffset);
        RightDrawerHost.Visibility = Visibility.Visible;
        ApplyDrawerColumnWidths(_savedDrawerLogicalWidth);
        RightPanelCollapsedChanged?.Invoke(this, false);

        // 展开时只需保证右栏有内容，不必重建整块月历（那会白闪一下）。
        if (refresh)
        {
            ShowDetailsForSelected();
        }

        var openEase = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1f),
            new Vector2(0.3f, 1f));
        var openSlide = compositor.CreateVector3KeyFrameAnimation();
        openSlide.Duration = TimeSpan.FromMilliseconds(180);
        openSlide.InsertKeyFrame(0f, startOffset);
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

    /// <summary>把时间取整到最近的 5 分钟，拖拽落点不需要秒级精度。</summary>
    private static TimeSpan RoundToFiveMinutes(TimeSpan time)
    {
        var minutes = (int)Math.Round(time.TotalMinutes / 5) * 5;
        return TimeSpan.FromMinutes(minutes % (24 * 60));
    }

    /// <summary>拖到没有时间的条目上时，弹窗让用户选一个时间。</summary>
    private async Task<DateTime?> PromptForTimeAsync(DateTime date)
    {
        if (Root.XamlRoot is null)
        {
            return null;
        }

        var theme = Root.RequestedTheme == ElementTheme.Default
            ? Appearance.ToElementTheme((ThemeMode)(_settings?.Current.ThemeMode ?? 0))
            : Root.RequestedTheme;

        var (confirmed, dueAt) = await SubtaskDuePrompt.ShowAsync(
            Root.XamlRoot, theme, date.Date.AddHours(9), "设置时间", CurrentTintMode());
        return confirmed ? dueAt ?? date.Date.AddHours(9) : null;
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Visibility != Visibility.Visible)
        {
            return;
        }

        if (_pendingDrawerResizeWidth is { } expectedWidth && Math.Abs(e.NewSize.Width - expectedWidth) < 1)
        {
            FinishDrawerResize();
        }

        // 手动把窗口拖窄到阈值以下：自动收起侧栏，但不联动宿主窗口改尺寸。
        // 之后即使再拖宽也不自动展开，需要用户手动点开关。
        if (!_rightPanelCollapsed &&
            _calendarWidthDuringDrawerResize is null &&
            e.NewSize.Width < AutoCollapseWidthLogical)
        {
            SetRightPanelCollapsed(true, refresh: false, notifyHost: false);
        }

        var widthChanged = Math.Abs(e.NewSize.Width - _lastLayoutWidth) >= 4;
        var heightChanged = Math.Abs(e.NewSize.Height - _lastLayoutHeight) >= 4;
        if (!widthChanged && !heightChanged)
        {
            return;
        }

        if (_calendarWidthDuringDrawerResize is not null)
        {
            _lastLayoutWidth = e.NewSize.Width;
            if (heightChanged)
            {
                _lastLayoutHeight = e.NewSize.Height;
            }

            ApplyResponsiveMetrics(GetUiScale());
            return;
        }

        if (!heightChanged)
        {
            // 只有宽度变了（抽屉开合改的就是宽度）：格子用星号列会自己随宽度重排，
            // 行高和字体缩放都只跟高度/总宽走，这时全量重建只会白闪一下，所以只做轻量更新。
            _lastLayoutWidth = e.NewSize.Width;
            ApplyResponsiveMetrics(GetUiScale());
            return;
        }

        _lastLayoutWidth = e.NewSize.Width;
        _lastLayoutHeight = e.NewSize.Height;

        // 高度变化（上下拖窗口边）时行高才需要重算，但全量重建 18 周的格子很重，
        // 每帧都做会卡。拖动过程中只更新字体/间距这类轻量指标，停手后再重建一次。
        ApplyResponsiveMetrics(GetUiScale());
        ScheduleSettledRefresh();
    }

    private DispatcherQueueTimer? _settleTimer;

    /// <summary>缩放停稳一小段时间后只重建一次月视图，避免拖动过程中反复全量重建。</summary>
    private void ScheduleSettledRefresh()
    {
        if (_settleTimer is null)
        {
            _settleTimer = DispatcherQueue.CreateTimer();
            _settleTimer.Tick += (_, _) =>
            {
                _settleTimer.Stop();
                Refresh();
            };
        }

        _settleTimer.Interval = TimeSpan.FromMilliseconds(140);
        _settleTimer.Stop();
        _settleTimer.Start();
    }
}