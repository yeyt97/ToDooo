using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ToDooo.Interop;
using ToDooo.Models;
using ToDooo.ViewModels;
using Windows.Graphics;
using Windows.UI;

namespace ToDooo.Views;

/// <summary>
/// 新建 / 编辑待办。做成独立的小窗口（而不是 ContentDialog）：
/// ContentDialog 会被限制在主窗口客户区里，主窗口一窄就挤得显示不全，也没法用窗口级磨砂。
/// </summary>
public sealed partial class TodoEditorDialog : Window
{
    private const int LogicalWidth = 420;

    // 截止时间收起/展开是两种高度，窗口跟着长，别留一大片空白。
    private const int CollapsedHeight = 392;
    private const int ExpandedHeight = 486;

    // 让后续逻辑排在关窗之后的下一轮消息里跑，别卡在关窗过程中重入 UI。
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Button> _tagButtons = new();
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private DesktopAcrylicController? _acrylic;
    private int _tagIndex = -1;
    private bool _finished;
    private bool _saved;

    public TodoEditorDialog() : this(string.Empty)
    {
    }

    /// <summary>新建：把添加框里已经敲好的草稿带过来。</summary>
    public TodoEditorDialog(string initialTitle)
    {
        InitializeComponent();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));

        ConfigureWindow();
        // 弹窗面积小、下面压着主界面，tint 比主窗口厚一点才不会把身后的待办文字透出来。
        _acrylic = WindowAcrylic.Apply(this, tintOpacity: 0.5f);

        HeaderTitle.Text = "新建待办";
        TitleBox.Text = initialTitle ?? string.Empty;

        // 新建时选了日期就带当前时刻，避免默认落到 00:00 立刻逾期。
        DueTime.SelectedTime = RoundToFiveMinutes(DateTime.Now.TimeOfDay);
        BuildTagButtons();
        UpdateTagVisuals();
        ApplyDueVisibility();

        Closed += OnClosed;
    }

    public TodoEditorDialog(TodoItem item) : this(item.Title)
    {
        HeaderTitle.Text = "编辑待办";
        TitleBox.Text = item.Title;

        if (item.DueAt is { } due)
        {
            DueToggle.IsOn = true;
            DueDate.Date = new DateTimeOffset(due.Date);
            DueTime.SelectedTime = due.TimeOfDay;
        }

        RecurrenceBox.SelectedIndex = (int)item.Recurrence;
        ImportantToggle.IsOn = item.IsImportant;
        _tagIndex = item.TagIndex;
        UpdateTagVisuals();
        ApplyDueVisibility();
    }

    /// <summary>true = 保存，false = 取消 / 直接关掉窗口。</summary>
    public Task<bool> Result => _result.Task;

    public string TodoTitle => TitleBox.Text.Trim();

    public DateTime? DueAt
    {
        get
        {
            // 没打开「截止时间」就是无期限，日期控件里残留的值一律不算。
            if (!DueToggle.IsOn || DueDate.Date is not { } date)
            {
                return null;
            }

            return date.Date + (DueTime.SelectedTime ?? TimeSpan.Zero);
        }
    }

    public TodoRecurrence Recurrence => (TodoRecurrence)Math.Max(0, RecurrenceBox.SelectedIndex);

    public bool IsImportant => ImportantToggle.IsOn;

    public int TagIndex => _tagIndex;

    /// <summary>先摆到主窗口正中间再激活；owner 为空就只按当前显示器兜底。</summary>
    public void ShowOver(AppWindow? owner)
    {
        _appWindow.Show();
        MoveNextTo(owner);
        Activate();

        TitleBox.Focus(FocusState.Programmatic);
        TitleBox.SelectAll();
    }

    private void ConfigureWindow()
    {
        _appWindow.Title = "待办";
        _appWindow.IsShownInSwitchers = false;

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            // 保留系统尺寸边框：Win11 才会给窗口裁圆角、画阴影。
            presenter.SetBorderAndTitleBar(true, true);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        var dark = 1;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        var scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        _appWindow.Resize(new SizeInt32(
            (int)Math.Round(LogicalWidth * scale),
            (int)Math.Round(CollapsedHeight * scale)));

        // 自绘标题栏：只留系统的最小化/最大化/关闭按钮里的「关闭」，标题由 XAML 画。
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(HeaderBar);
        _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        HeaderBar.Padding = new Thickness(18, 0, Math.Max(12, _appWindow.TitleBar.RightInset), 0);
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

        // 主窗口贴边时，居中出来的位置可能半截在屏幕外，夹回工作区。
        _appWindow.Move(new PointInt32(
            Math.Clamp(point.X, area.X, area.X + Math.Max(0, area.Width - size.Width)),
            Math.Clamp(point.Y, area.Y, area.Y + Math.Max(0, area.Height - size.Height))));
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _finished = true;

        // 结果在这里统一给出：窗口已经关掉了，调用方再继续。
        // 之前在点按钮时就把结果抛出去，调用方会立刻抢焦点、刷主界面，
        // 而窗口还没真正消失，看起来就是「点取消时闪一下」。
        _result.TrySetResult(_saved);

        // 不要在 Closed 里 Dispose 亚克力控制器：窗口的合成目标正在销毁，
        // 这时释放会让进程直接挂掉（0xc000027b，CoreMessagingXP 里崩）。
        // 控制器交给 GC 回收即可，弹窗开得很少，这点驻留无所谓。
        _acrylic = null;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e) => Finish(true);

    private void TitleBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // 单行标题框里回车就是保存，和主界面添加框的手感一致。
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            Finish(true);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Finish(false);

    private void SaveAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Finish(true);
    }

    private void CancelAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Finish(false);
    }

    private void Finish(bool saved)
    {
        if (_finished)
        {
            return;
        }

        // 标题空着就不保存：既不关窗，也把焦点丢回输入框提示一下。
        if (saved && TodoTitle.Length == 0)
        {
            TitleBox.Focus(FocusState.Programmatic);
            return;
        }

        // 先隐藏再关：WinUI 关窗瞬间合成目标会先变黑一帧（点取消肉眼可见「闪一下」），
        // 隐藏后窗口不再参与合成，黑帧就看不到了。结果仍在 Closed 里给。
        _saved = saved;
        _finished = true;
        _appWindow.Hide();
        DispatcherQueue.TryEnqueue(Close);
    }

    private void DueToggle_Toggled(object sender, RoutedEventArgs e) => ApplyDueVisibility();

    // 打开才显示日期/时间，同时把窗口高度换成展开态，收起时不留空白。
    private void ApplyDueVisibility()
    {
        var on = DueToggle.IsOn;
        DueSection.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        // 打开就默认今天（时间已在构造时预填当前时刻），不用再多点一次日期；
        // 已经有值的（编辑旧待办、或之前选过）保持原样。
        if (on && DueDate.Date is null)
        {
            DueDate.Date = DateTimeOffset.Now.Date;
        }

        var scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        _appWindow.Resize(new SizeInt32(
            (int)Math.Round(LogicalWidth * scale),
            (int)Math.Round((on ? ExpandedHeight : CollapsedHeight) * scale)));
    }

    private static TimeSpan RoundToFiveMinutes(TimeSpan value)
    {
        var minutes = (int)Math.Round(value.TotalMinutes / 5.0) * 5;
        return TimeSpan.FromMinutes(minutes % (24 * 60));
    }

    private void BuildTagButtons()
    {
        for (var i = 0; i < MainViewModel.TagColors.Length; i++)
        {
            var color = Color.FromArgb(
                255,
                Convert.ToByte(MainViewModel.TagColors[i].Substring(1, 2), 16),
                Convert.ToByte(MainViewModel.TagColors[i].Substring(3, 2), 16),
                Convert.ToByte(MainViewModel.TagColors[i].Substring(5, 2), 16));

            AddTagButton(i, new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(7),
                Background = new SolidColorBrush(color),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        AddTagButton(-1, new FontIcon
        {
            Glyph = "\uE711",
            FontSize = 9,
            Opacity = 0.55,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
    }

    private void AddTagButton(int index, UIElement content)
    {
        var host = new Button
        {
            Style = (Style)Root.Resources["TagRingButton"],
            Content = content,
            Tag = index,
            BorderBrush = new SolidColorBrush(Colors.Transparent),
            Background = new SolidColorBrush(Colors.Transparent),
        };

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            host, index < 0 ? "清除标签" : $"标签 {index + 1}");

        host.Click += (s, _) =>
        {
            var tag = (int)((Button)s!).Tag;
            // 点已选中的色条等于取消标签，和主界面顶部的标签筛选一个手感。
            _tagIndex = tag < 0 || _tagIndex == tag ? -1 : tag;
            UpdateTagVisuals();
        };

        _tagButtons.Add(host);
        TagPanel.Children.Add(host);
    }

    private void UpdateTagVisuals()
    {
        for (var i = 0; i < _tagButtons.Count; i++)
        {
            var index = i == _tagButtons.Count - 1 ? -1 : i;
            var selected = _tagIndex == index;
            // 选中用一圈亮描边 + 淡底色：系统强调色可能是灰的，只靠配色看不出选没选。
            ((SolidColorBrush)_tagButtons[i].BorderBrush).Color = selected
                ? Color.FromArgb(255, 0xE9, 0xEC, 0xF0)
                : Color.FromArgb(0, 0, 0, 0);
            ((SolidColorBrush)_tagButtons[i].Background).Color = selected
                ? Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0, 0, 0, 0);
        }
    }
}
