using System.IO;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using ToDooo.Interop;
using ToDooo.Models;
using ToDooo.Services;
using ToDooo.ViewModels;
using ToDooo.Views;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using WinRT;

namespace ToDooo;

public sealed partial class MainWindow : Window
{
    private void FocusAddBox() => OpenAddBar();

    private void AddTaskButton_Click(object sender, RoutedEventArgs e)
    {
        // 输入框已经开着就别再切一次，否则会先收起再弹出，闪一下。
        if (AddBar.Visibility != Visibility.Visible)
        {
            OpenAddBar();
        }
    }

    private void OpenAddBar()
    {
        AddBar.Visibility = Visibility.Visible;
        AddTaskButton.IsEnabled = false;
        AddTextBox.Focus(FocusState.Programmatic);
    }
    private void CloseAddBar()
    {
        // 收起时 TextBox 会失焦，别把残留文字当草稿提交。
        _suppressAddCommit = true;
        try
        {
            AddTextBox.Text = string.Empty;
            AddBar.Visibility = Visibility.Collapsed;
            AddTaskButton.IsEnabled = true;
        }
        finally
        {
            _dispatcher.TryEnqueue(() => _suppressAddCommit = false);
        }
    }

    private bool _textComposing;

    private void AddTextBox_TextCompositionStarted(TextBox sender, TextCompositionStartedEventArgs args) =>
        _textComposing = true;

    private void AddTextBox_TextCompositionEnded(TextBox sender, TextCompositionEndedEventArgs args) =>
        _textComposing = false;

    private void AddTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var hasText = !string.IsNullOrEmpty(AddTextBox.Text);
        ClearAddButton.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearAddButton_Click(object sender, RoutedEventArgs e)
    {
        AddTextBox.Text = string.Empty;
        FocusAddBox();
    }

    private void AddTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && _textComposing)
        {
            // 输入法还在组词，这一下回车是给输入法的；等组词结束后再提交，避免重复添加。
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            if (CommitAddDraft() && ViewModel.Items.Count > 0)
            {
                TodoList.ScrollIntoView(ViewModel.Items[0]);
            }

            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CloseAddBar();
        }
    }

    // 窗口失活（切到别的程序、点桌面）时 TextBox 不会触发 LostFocus，得靠这里补。
    private void MainWindow_Activated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState != WindowActivationState.Deactivated)
        {
            return;
        }

        // 右键菜单是独立窗口：弹出时主窗口先失活、菜单随后才 Opening。
        // 隔一拍再看，菜单确实开着就说明用户只是要粘贴，不是要离开。
        var timer = _dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(180);
        timer.IsRepeating = false;
        timer.Tick += (t, _) =>
        {
            t.Stop();
            if (_composerMenuOpen)
            {
                return;
            }

            if (!_suppressAddCommit &&
                AddBar.Visibility == Visibility.Visible &&
                string.IsNullOrWhiteSpace(AddTextBox.Text))
            {
                CloseAddBar();
            }
            else
            {
                CommitAddDraft();
            }

            CommitSubtaskAddDraft();
        };
        timer.Start();
    }

    // 离焦即提交：点列表、点窗口别处、切走窗口，敲了一半的草稿都不会再悄悄丢掉。
    private void AddTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // 点「清除」「详细添加」造成的离焦不算提交：
        // 前者是要丢弃草稿，后者要把草稿带进对话框，先提交就会重复添加两次。
        // 口径放宽到整个输入条：焦点只是跑到同一条里的按钮上，就还没离开这个「添加动作」。
        var focused = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
        if (focused is not null && IsWithin(focused, AddBar))
        {
            return;
        }

        // 右键菜单是延迟挂上来的浮层，失焦先于它发生；隔一小段再判定。
        ScheduleComposerFocusCheck(
            () => IsWithinOrSelf(FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject, AddBar),
            HandleAddBarFocusExit,
            () =>
            {
                if (AddBar.Visibility == Visibility.Visible)
                {
                    AddTextBox.Focus(FocusState.Programmatic);
                }
            });
    }

    /// <summary>
    /// 失焦后隔一拍再判定：右键菜单（会抢焦点）比失焦晚到，直接判会误收起。
    /// 判定时若菜单正开着，就登记回调，等菜单关闭事件触发时再执行（把焦点还给输入框）。
    /// </summary>
    private void ScheduleComposerFocusCheck(Func<bool> stillInside, Action leftForReal, Action menuDismissed)
    {
        var timer = _dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(200);
        timer.IsRepeating = false;
        timer.Tick += (t, _) =>
        {
            t.Stop();
            if (_composerMenuOpen)
            {
                _pendingMenuDismissed = menuDismissed;
                return;
            }

            if (!stillInside())
            {
                leftForReal();
            }
        };
        timer.Start();
    }

    private void HandleAddBarFocusExit()
    {
        if (AddBar.Visibility != Visibility.Visible)
        {
            return;
        }

        var focused = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
        if (focused is not null && (IsWithin(focused, AddBar) || ReferenceEquals(focused, AddTextBox)))
        {
            return;
        }

        // 空内容离焦：直接收起，不占位置。
        if (string.IsNullOrWhiteSpace(AddTextBox.Text))
        {
            CloseAddBar();
            return;
        }

        CommitAddDraft();
    }

    /// <summary>
    /// 给输入框的右键菜单挂开合事件。控件默认就带一个 TextCommandBarFlyout，
    /// 直接复用它（换新实例会丢掉选区等内置行为）。
    /// </summary>
    private void AttachComposerMenu(TextBox box)
    {
        var flyout = box.ContextFlyout as TextCommandBarFlyout;
        if (flyout is null)
        {
            flyout = new TextCommandBarFlyout();
            box.ContextFlyout = flyout;
        }

        if (!_hookedFlyouts.Add(flyout))
        {
            return;
        }

        flyout.Opening += (_, _) =>
        {
            _composerMenuOpen = true;
            _pendingMenuDismissed = null;
        };
        flyout.Closed += (_, _) =>
        {
            _composerMenuOpen = false;
            var pending = _pendingMenuDismissed;
            _pendingMenuDismissed = null;
            pending?.Invoke();
        };
    }

    private readonly HashSet<TextCommandBarFlyout> _hookedFlyouts = new();
    private bool _composerMenuOpen;
    private Action? _pendingMenuDismissed;

    private static bool IsWithinOrSelf(DependencyObject? node, DependencyObject ancestor) =>
        node is not null && (ReferenceEquals(node, ancestor) || IsWithin(node, ancestor));

    // 回车和离焦共用的一套提交逻辑；返回是否真的加了一条。
    private bool CommitAddDraft()
    {
        // 编辑器弹窗打开期间不提交：草稿归弹窗管，主窗口这时的「失焦」只是被弹窗抢了前台。
        if (_suppressAddCommit)
        {
            return false;
        }

        var title = AddTextBox.Text;
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var parsed = ToDooo.Helpers.NaturalDateParser.ParseRich(title, DateTime.Now);
        if (parsed.DueAt is not null || parsed.Recurrence != TodoRecurrence.None || parsed.IsImportant)
        {
            ViewModel.AddItem(new TodoItem
            {
                Title = parsed.Title,
                DueAt = parsed.DueAt,
                Recurrence = parsed.Recurrence,
                IsImportant = parsed.IsImportant,
            });
        }
        else
        {
            ViewModel.Add(title);
        }

        AddTextBox.Text = string.Empty;
        UpdateFilterVisuals();
        UpdateTagVisuals();
        CloseAddBar();
        return true;
    }

    private static bool IsWithin(DependencyObject node, DependencyObject ancestor)
    {
        for (DependencyObject? current = node; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private void AddDetailButton_Click(object sender, RoutedEventArgs e) => _ = AddDetailedAsync();

    private async Task AddDetailedAsync()
    {
        // 把输入框里已经敲好的草稿带进详细新建，取消时原样保留。
        // 草稿里的日期/时间也一并识别好，弹窗里直接预填，用户可以再改。
        var draft = AddTextBox.Text;
        var parsed = ToDooo.Helpers.NaturalDateParser.ParseRich(draft, DateTime.Now);
        var dialog = new TodoEditorDialog(
            _settings,
            parsed.Title,
            parsed.DueAt,
            parsed.Recurrence,
            parsed.IsImportant);

        if (await ShowEditorAsync(dialog))
        {
            var item = new TodoItem
            {
                Title = dialog.TodoTitle,
                Note = dialog.TodoNote,
                DueAt = dialog.DueAt,
                Recurrence = dialog.Recurrence,
                IsImportant = dialog.IsImportant,
                TagIndex = dialog.TagIndex,
            };

            foreach (var subtaskDraft in dialog.Subtasks)
            {
                var title = subtaskDraft.Title.Trim();
                if (title.Length == 0)
                {
                    continue;
                }

                item.Subtasks.Add(new SubtaskItem
                {
                    Title = title,
                    Note = (subtaskDraft.Note ?? string.Empty).Trim(),
                    IsDone = subtaskDraft.Done,
                    DueAt = subtaskDraft.DueAt,
                    SortOrder = item.Subtasks.Count,
                });
            }

            ViewModel.AddItem(item);
            AddTextBox.Text = string.Empty;
            UpdateFilterVisuals();
            UpdateTagVisuals();
            CloseAddBar();
        }
        else if (string.IsNullOrWhiteSpace(AddTextBox.Text))
        {
            CloseAddBar();
        }
        else
        {
            FocusAddBox();
        }
    }

    private async Task AddTaskAtAsync(DateTime date, AppWindow? owner = null)
    {
        var dialog = new TodoEditorDialog(_settings, string.Empty, date.Date + DateTime.Now.TimeOfDay);
        var created = await ShowEditorAsync(dialog, owner);
        RestoreOwnerFocus(owner);
        if (!created)
        {
            return;
        }

        var item = new TodoItem
        {
            Title = dialog.TodoTitle,
            Note = dialog.TodoNote,
            DueAt = dialog.DueAt,
            Recurrence = dialog.Recurrence,
            IsImportant = dialog.IsImportant,
            TagIndex = dialog.TagIndex,
        };

        foreach (var draft in dialog.Subtasks)
        {
            var title = draft.Title.Trim();
            if (title.Length == 0)
            {
                continue;
            }

            item.Subtasks.Add(new SubtaskItem
            {
                Title = title,
                Note = (draft.Note ?? string.Empty).Trim(),
                IsDone = draft.Done,
                DueAt = draft.DueAt,
                SortOrder = item.Subtasks.Count,
            });
        }

        ViewModel.AddItem(item);
        UpdateFilterVisuals();
        UpdateTagVisuals();
    }

    /// <summary>
    /// 显示编辑窗口并等结果。同一时间只允许一个：已经开着就把那个提到前台，
    /// 返回 false（本次没有新建任何东西），避免叠出好几个窗口。
    /// </summary>
    private async Task<bool> ShowEditorAsync(TodoEditorDialog dialog, AppWindow? owner = null)
    {
        if (_editor is not null)
        {
            TopmostDialogIfDocked(_editor);
            _editor.Activate();
            return false;
        }

        _editor = dialog;

        // 弹窗是独立窗口，打开时主窗口会失活；关掉这段自动提交，
        // 否则「离焦即保存」会先把草稿存成一条，再被弹窗存第二条。
        _suppressAddCommit = true;
        try
        {
            dialog.ShowOver(owner ?? _appWindow);
            TopmostDialogIfDocked(dialog);
            dialog.Activate();
            return await dialog.Result;
        }
        finally
        {
            _suppressAddCommit = false;
            _editor = null;
        }
    }

    // DataTemplate 刚生成时 x:Bind 可能还没求值，IsChecked 会短暂停在 null（三态）。
    private void DoneCheckBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: TodoItem item } box)
        {
            box.IsChecked = item.IsDone;
        }
    }

    private void DoneCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: TodoItem item } box)
        {
            ViewModel.SetDone(item, box.IsChecked == true);
            // SetDone 会重建筛选视图，先把当前控件状态锁回数据值，避免模板复用时状态漂移。
            box.IsChecked = item.IsDone;
            UpdateFilterVisuals();
        }
    }

    // 点整行只展开/收起子任务，不再打开编辑窗口。
    private void TodoList_ItemClick(object sender, ItemClickEventArgs e)
    {
        // 刚拖完子任务松手时，别把这次释放再当成「点行展开」。
        if (Environment.TickCount64 - _lastSubtaskDropTicks < 400)
        {
            return;
        }

        // 结束编辑的这一次点击只负责保存，不顺手把子任务面板折叠起来。
        if (_pressWhileEditing)
        {
            return;
        }

        if (e.ClickedItem is TodoItem item && (item.HasSubtasks || item.SubtasksExpanded))
        {
            // 收起面板前先把没确认的子任务输入落下来，别跟着面板一起消失。
            if (item.SubtasksExpanded)
            {
                CommitSubtaskAddDraft();
            }

            item.SubtasksExpanded = !item.SubtasksExpanded;
            ResetSubtaskDragGuardIfCollapsed(item);
            ViewModel.NotifySubtaskExpansionChanged();
        }
    }

    private bool _pressWhileEditing;

    private void ResetSubtaskDragGuardIfCollapsed(TodoItem item)
    {
        if (item.SubtasksExpanded)
        {
            return;
        }

        item.SubtaskAddActive = false;

        // 子任务区随收起消失时 PointerExited 不一定会到，手动把拖动开关还原。
        _pointerOverSubtasks = false;
        if (!_subtaskDragActive)
        {
            TodoList.CanDragItems = true;
            TodoList.CanReorderItems = true;
        }
    }

    private void SubtaskAddControl_Loaded(object sender, RoutedEventArgs e)
    {
        SetSubtaskProgressMode(sender, adding: false);
    }

    // 用透明度切换而不是显隐，避免切换瞬间布局重排导致卡片高度跳动或指针状态卡住。
    private static void SetSubtaskProgressMode(object sender, bool adding)
    {
        if (sender is not DependencyObject root)
        {
            return;
        }

        if (FindTaggedChild<StackPanel>(root, "SubtaskProgressView") is { } progress)
        {
            progress.Opacity = adding ? 0 : 1;
        }

        if (FindTaggedChild<TextBlock>(root, "SubtaskProgressAdd") is { } hint)
        {
            hint.Opacity = adding ? 1 : 0;
        }
    }

    // 平时显示“2/5”，鼠标悬停整张卡片时变成“子任务”，点一下直接开始输入。
    private void SubtaskAddButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TodoItem item } button)
        {
            SetSubtaskProgressMode(button, adding: false);
            item.SubtasksExpanded = true;
            item.SubtaskAddActive = true;
            ViewModel.NotifySubtaskExpansionChanged();
            DispatcherQueue.TryEnqueue(() => FocusSubtaskAddBox(item));
        }
    }

    private async void SubtaskCheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox box && box.DataContext is SubtaskItem subtask)
        {
            if (FindOwnerTodo(box) is { } item)
            {
                var done = box.IsChecked == true;
                ViewModel.SetSubtaskDone(item, subtask, done);
                if (done && !item.IsDone && item.Subtasks.Count > 0 && item.Subtasks.All(s => s.IsDone))
                {
                    var dialog = CreateCardDialog(
                        "子任务已全部完成",
                        $"「{item.Title}」的子任务已全部完成，是否同时完成主任务？",
                        "完成主任务",
                        defaultButton: ContentDialogButton.Primary);
                    dialog.CloseButtonText = "暂不";
                    if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    {
                        ViewModel.SetDone(item, true);
                        UpdateFilterVisuals();
                    }
                }
            }
        }
    }

    private SubtaskItem? _draggedSubtask;
    private TodoItem? _draggedOwner;
    private bool _pointerOverSubtasks;

    // 指针在子任务区域时先禁掉外层列表的拖动，否则会变成拖动整张卡片。
    private void SubtaskListArea_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerOverSubtasks = true;
        TodoList.CanDragItems = false;
        TodoList.CanReorderItems = false;
    }

    private void SubtaskListArea_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement area)
        {
            var point = e.GetCurrentPoint(area).Position;
            if (point.X >= 0 && point.Y >= 0 &&
                point.X <= area.RenderSize.Width && point.Y <= area.RenderSize.Height)
            {
                return;
            }
        }

        // 子任务拖到列表外时指针会离开这个区域，但拖动还没结束，别急着把列表拖动打开。
        if (_subtaskDragActive)
        {
            return;
        }

        _pointerOverSubtasks = false;
        TodoList.CanDragItems = true;
        TodoList.CanReorderItems = true;
    }

    private void TodoList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (_pointerOverSubtasks || _subtaskDragActive || _pressOnSubtask)
        {
            e.Cancel = true;
            return;
        }

        if (_calendarExpanded && e.Items.FirstOrDefault() is TodoItem item)
        {
            _calendarHostWindow?.Calendar.BeginExternalDrag(item, null, e.Data);
        }
    }

    private const string SubtaskDragKey = "ToDoooSubtask";
    private long _lastSubtaskDropTicks;
    private bool _subtaskDragActive;
    private bool _pressOnSubtask;

    private void SubtaskRow_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: SubtaskItem subtask })
        {
            _draggedSubtask = subtask;
            _draggedOwner = FindOwnerTodo(sender);
            _subtaskDragActive = true;
            if (sender is Grid row)
            {
                // 拖动中的行给一块圆角底色，别让它只是半透明地悬在空中。
                row.Opacity = 0.85;
                row.Background = PaletteBrush("ItemCardBrush");
                _draggedRow = row;
            }
            else if (sender is UIElement element)
            {
                element.Opacity = 0.55;
            }

            args.Data.Properties[SubtaskDragKey] = true;
            args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;

            if (_calendarExpanded && _draggedOwner is { } owner)
            {
                _calendarHostWindow?.Calendar.BeginExternalDrag(owner, subtask, args.Data);
            }
        }
    }

    // 子任务拖动时整个窗口都接受移动，避免指针划过行间空隙时闪“禁止”图标。
    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (!_subtaskDragActive)
        {
            return;
        }

        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        e.DragUIOverride.IsCaptionVisible = false;
        e.DragUIOverride.IsGlyphVisible = false;
    }

    private void Root_Drop(object sender, DragEventArgs e)
    {
        _calendarHostWindow?.Calendar.EndExternalDrag();
        if (_draggedOwner is { } owner && _draggedSubtask is not null)
        {
            ViewModel.CommitSubtaskOrder(owner);
        }
    }

    private Grid? _draggedRow;

    /// <summary>拖动结束后把源行的底色和透明度还原。</summary>
    private void RestoreDraggedRowVisual()
    {
        if (_draggedRow is not { } row)
        {
            return;
        }

        _draggedRow = null;
        try
        {
            row.Opacity = 1;
            row.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
        catch
        {
            // 行可能已经随列表重建被销毁，忽略即可。
        }
    }

    private void SubtaskRow_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Properties.ContainsKey(SubtaskDragKey))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
            return;
        }

        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        e.DragUIOverride.IsCaptionVisible = false;
        e.DragUIOverride.IsGlyphVisible = false;

        // 和主任务拖排序一样：拖到哪一行，行就实时让位。
        if (_draggedSubtask is not { } dragged ||
            sender is not Grid row ||
            row.DataContext is not SubtaskItem target ||
            dragged == target ||
            FindOwnerTodo(row) is not { } item)
        {
            return;
        }

        var list = item.Subtasks;
        var from = list.IndexOf(dragged);
        var to = list.IndexOf(target);
        if (from < 0 || to < 0)
        {
            return;
        }

        var point = e.GetPosition(row);
        var index = point.Y <= row.ActualHeight / 2 ? to : to + 1;
        if (from < index)
        {
            index--;
        }

        ViewModel.MoveSubtaskLive(item, dragged, index);
    }

    private void SubtaskRow_DropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        RestoreDraggedRowVisual();
        sender.Opacity = 1;
        _draggedSubtask = null;
        _draggedOwner = null;
        _subtaskDragActive = false;
        _pointerOverSubtasks = false;
        _lastSubtaskDropTicks = Environment.TickCount64;
        TodoList.CanDragItems = true;
        TodoList.CanReorderItems = true;
        _calendarHostWindow?.Calendar.EndExternalDrag();
    }

    private void SubtaskRow_Drop(object sender, DragEventArgs e)
    {
        if (_draggedSubtask is not { } dragged ||
            sender is not Grid row ||
            !e.DataView.Properties.ContainsKey(SubtaskDragKey) ||
            row.DataContext is not SubtaskItem target ||
            dragged == target ||
            FindOwnerTodo(row) is not { } item)
        {
            return;
        }

        // 顺序已经在 DragOver 里实时排好，这里只落库。
        ViewModel.CommitSubtaskOrder(item);
        _draggedSubtask = null;
    }

    private static bool IsInsideSubtask(DependencyObject? start)
    {
        var current = start;
        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: SubtaskItem })
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static bool IsInsideTagged(DependencyObject? start, string tag)
    {
        var current = start;
        while (current is not null)
        {
            if (current is FrameworkElement element && element.Tag is string value && value == tag)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static TodoItem? FindOwnerTodo(DependencyObject start)
    {
        var current = start;
        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: TodoItem item })
            {
                return item;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private SubtaskItem? _menuSubtask;

    private void SubtaskFlyout_Opening(object sender, object e)
    {
        _contextMenuOpen = true;
        CancelDockRetract();

        if (sender is MenuFlyout { Target: FrameworkElement target } &&
            target.DataContext is SubtaskItem subtask)
        {
            _menuSubtask = subtask;
            _menuItem = FindOwnerTodo(target);
        }
    }

    private async void SubtaskDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_menuSubtask is not { } subtask || _menuItem is not { } item)
        {
            return;
        }

        await ConfirmDeleteSubtaskAsync(item, subtask);
    }

    private async void SubtaskEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_menuSubtask is not { } subtask || _menuItem is not { } item)
        {
            return;
        }

        if (_subtaskEditor is not null)
        {
            TopmostDialogIfDocked(_subtaskEditor);
            _subtaskEditor.Activate();
            return;
        }

        var dialog = new SubtaskEditorDialog(
            _settings,
            subtask.Title,
            subtask.Note,
            subtask.DueAt,
            subtask.IsDone);
        _subtaskEditor = dialog;
        _suppressAddCommit = true;
        _dialogOpen = true;
        CancelDockRetract();
        try
        {
            dialog.ShowOver(_appWindow);
            TopmostDialogIfDocked(dialog);
            dialog.Activate();
            if (await dialog.Result)
            {
                ViewModel.UpdateSubtask(
                    item,
                    subtask,
                    dialog.SubtaskTitle,
                    dialog.SubtaskNote,
                    dialog.DueAt,
                    dialog.IsDone);
            }
        }
        finally
        {
            _suppressAddCommit = false;
            _dialogOpen = false;
            _subtaskEditor = null;
        }
    }

    private async void SubtaskRowDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SubtaskItem subtask } button &&
            FindOwnerTodo(button) is { } item)
        {
            await ConfirmDeleteSubtaskAsync(item, subtask);
        }
    }

    private async Task ConfirmDeleteSubtaskAsync(TodoItem item, SubtaskItem subtask)
    {
        var dialog = CreateCardDialog("删除子任务", $"确定删除「{subtask.Title}」吗？", "删除");
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.DeleteSubtask(item, subtask);
        }
    }

    private void SubtaskRow_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        ShowSubtaskDeleteButton(sender, true);

    private void SubtaskRow_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid row)
        {
            var point = e.GetCurrentPoint(row).Position;
            if (point.X >= 0 && point.Y >= 0 &&
                point.X <= row.ActualWidth && point.Y <= row.ActualHeight)
            {
                return;
            }
        }

        ShowSubtaskDeleteButton(sender, false);
    }

    private static void ShowSubtaskDeleteButton(object sender, bool show)
    {
        if (sender is not DependencyObject root)
        {
            return;
        }

        if (FindTaggedChild<Button>(root, "SubtaskDeleteButton") is { } button)
        {
            button.Opacity = show ? 1 : 0;
            button.IsHitTestVisible = show;
        }
    }

    /// <summary>弹框统一用卡片色板：和窗口主题、透明度保持一致。</summary>
    private ContentDialog CreateCardDialog(
        string title,
        object content,
        string primaryButtonText,
        bool primaryEnabled = true,
        ContentDialogButton defaultButton = ContentDialogButton.Close)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "取消",
            DefaultButton = defaultButton,
            IsPrimaryButtonEnabled = primaryEnabled,
        };

        // 弹框本身不继承主窗口的 RequestedTheme，浅色窗口在深色系统下会显示成白字。
        dialog.RequestedTheme = Root.RequestedTheme == ElementTheme.Default
            ? Appearance.ToElementTheme((ThemeMode)_settings.Current.ThemeMode)
            : Root.RequestedTheme;
        ThemePalette.ApplyTextControlPalette(dialog.Resources);

        // 弹框有可能落在独立弹层里，指针会离开主窗口；这期间别让窗口自动收回。
        dialog.Opened += (_, _) =>
        {
            _dialogOpen = true;
            CancelDockRetract();
        };
        dialog.Closed += (_, _) =>
        {
            _dialogOpen = false;
        };

        dialog.Resources["ContentDialogBackground"] = ThemePalette.CreateDialogAcrylicBrush(
            dialog.RequestedTheme == ElementTheme.Dark, CurrentTintMode());
        dialog.Resources["ContentDialogBorderBrush"] = Application.Current.Resources["CardStrokeBrush"];
        dialog.Resources["ContentDialogCornerRadius"] = new CornerRadius(8);
        dialog.Resources["ContentDialogSmokeFill"] = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x00, 0x00));
        return dialog;
    }

    // 卡片悬停：提亮卡片，并让未标记重要的行浮出星标按钮（已标记的本来就在亮着）。
    private void TodoCard_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border card)
        {
            return;
        }

        card.Background = (Brush)Application.Current.Resources["ItemCardHoverBrush"];
        if (FindTaggedChild<Button>(card, "ItemActions") is { } star)
        {
            star.Opacity = 1;
            star.IsHitTestVisible = true;
        }

        // 子任务按钮和星标同步：鼠标进卡片就把“x/y”换成“子任务”。
        if (FindTaggedChild<Button>(card, "SubtaskAddControl") is { } subtaskButton)
        {
            SetSubtaskProgressMode(subtaskButton, adding: true);
        }
    }

    private void TodoCard_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border card)
        {
            return;
        }

        card.ClearValue(Border.BackgroundProperty);

        // 星标和铅笔一样只跟悬停显隐，标记后的状态由标题左侧的星形图标常驻表示。
        if (FindTaggedChild<Button>(card, "ItemActions") is { } star)
        {
            star.Opacity = 0;
            star.IsHitTestVisible = false;
        }

        if (FindTaggedChild<Button>(card, "SubtaskAddControl") is { } subtaskButton)
        {
            SetSubtaskProgressMode(subtaskButton, adding: false);
        }
    }

    private static T? FindTaggedChild<T>(DependencyObject root, string tag) where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed && typed.Tag is string value && value == tag)
            {
                return typed;
            }

            if (FindTaggedChild<T>(child, tag) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    // 星标按钮每次被模板复用时不用额外处理：Opacity / IsHitTestVisible 都绑在 IsImportant 上，
    // 上下文菜单里标记重要后也能立刻亮起，不依赖悬停。

    private void QuickImportant_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TodoItem item })
        {
            ViewModel.ToggleImportant(item);
            UpdateFilterVisuals();

            // 不等绑定/列表刷新，直接给当前按钮一个即时反馈。
            if (sender is Button star)
            {
                // 指针就在这一行上，标记与否都先保持可见，移出时由 PointerExited 收回去。
                star.Opacity = 1;
                star.IsHitTestVisible = true;

                if (star.Content is FontIcon icon)
                {
                    icon.Glyph = item.IsImportant ? "\uE735" : "\uE734";
                    icon.Foreground = (Brush)Application.Current.Resources[
                        item.IsImportant ? "AccentBrush" : "TextMutedBrush"];
                }
            }
        }
    }

    // 左下角开关：一次展开/收起当前列表里所有带子任务的行。
    private void ToggleAllSubtasks_Click(object sender, RoutedEventArgs e)
    {
        var expand = !ViewModel.AreAllVisibleSubtasksExpanded;

        foreach (var item in ViewModel.Items)
        {
            if (!item.HasSubtasks)
            {
                continue;
            }

            item.SubtasksExpanded = expand;
            if (!expand)
            {
                ResetSubtaskDragGuardIfCollapsed(item);
            }
        }

        ViewModel.NotifySubtaskExpansionChanged();
    }

    private async void ClearCompleted_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.DoneCount == 0)
        {
            return;
        }

        var dialog = CreateCardDialog(
            "清除已完成",
            $"将删除 {ViewModel.DoneCount} 条已完成待办，删除后可在底部撤销。",
            "清除");

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var removed = ViewModel.ClearCompleted();
        UpdateFilterVisuals();
        if (removed.Count > 0)
        {
            ShowUndo($"已清除 {removed.Count} 条已完成", removed);
        }
    }

    private TodoItem? _menuItem;

    private void RowFlyout_Closed(object sender, object e) => _contextMenuOpen = false;

    private void SubtaskFlyout_Closed(object sender, object e) => _contextMenuOpen = false;

    private void RowFlyout_Opening(object sender, object e)
    {
        _contextMenuOpen = true;
        CancelDockRetract();

        if (sender is not MenuFlyout { Target: FrameworkElement target } flyout)
        {
            return;
        }

        _menuItem = target.DataContext as TodoItem;

        foreach (var entry in flyout.Items)
        {
            if (_menuItem is null)
            {
                break;
            }

            if (entry is MenuFlyoutSubItem { Text: "截止时间" } dueMenu)
            {
                var hasDue = _menuItem.DueAt is not null;
                foreach (var subEntry in dueMenu.Items)
                {
                    if (subEntry is MenuFlyoutSeparator sep)
                    {
                        sep.Visibility = hasDue ? Visibility.Visible : Visibility.Collapsed;
                    }
                    else if (subEntry is MenuFlyoutItem { Text: "清除时间" } clearItem)
                    {
                        clearItem.Visibility = hasDue ? Visibility.Visible : Visibility.Collapsed;
                    }
                }
            }
            else if (entry is MenuFlyoutSubItem { Text: "设置颜色" } colorMenu)
            {
                PopulateTagColorMenu(colorMenu, _menuItem);
            }
            else if (entry is MenuFlyoutItem { Text: "10 分钟后再提醒" } snooze)
            {
                // 没有截止时间或已完成就没有提醒可推迟。
                snooze.Visibility = (_menuItem.IsDone || _menuItem.DueAt is null)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
        }
    }

    private static TimeSpan DefaultScheduleTime(DateTime? current)
    {
        if (current is { } due)
        {
            return due.TimeOfDay;
        }

        var minutes = (int)Math.Round(DateTime.Now.TimeOfDay.TotalMinutes / 5.0) * 5;
        return TimeSpan.FromMinutes(minutes % (24 * 60));
    }

    private void SubtaskNote_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock tb)
        {
            SyncSubtaskNoteToolTip(tb);
        }
    }

    private void SubtaskNote_IsTextTrimmedChanged(TextBlock sender, IsTextTrimmedChangedEventArgs args) =>
        SyncSubtaskNoteToolTip(sender);

    private static void SyncSubtaskNoteToolTip(TextBlock tb)
    {
        if (tb.IsTextTrimmed && !string.IsNullOrWhiteSpace(tb.Text))
        {
            ToolTipService.SetToolTip(tb, new ToolTip
            {
                Content = new TextBlock
                {
                    Text = tb.Text,
                    MaxWidth = 280,
                    TextWrapping = TextWrapping.Wrap,
                },
            });
        }
        else
        {
            ToolTipService.SetToolTip(tb, null);
        }
    }

    private void RowDueToday_Click(object sender, RoutedEventArgs e)
    {
        if (_menuItem is { } item)
        {
            ViewModel.ScheduleAt(item, DateTime.Today + DefaultScheduleTime(item.DueAt));
            UpdateFilterVisuals();
        }
    }

    private void RowDueTomorrow_Click(object sender, RoutedEventArgs e)
    {
        if (_menuItem is { } item)
        {
            ViewModel.ScheduleAt(item, DateTime.Today.AddDays(1) + DefaultScheduleTime(item.DueAt));
            UpdateFilterVisuals();
        }
    }

    private void RowDueNextMonday_Click(object sender, RoutedEventArgs e)
    {
        if (_menuItem is { } item)
        {
            var delta = ((int)DayOfWeek.Monday - (int)DateTime.Today.DayOfWeek + 7) % 7;
            if (delta == 0)
            {
                delta = 7;
            }

            ViewModel.ScheduleAt(item, DateTime.Today.AddDays(delta) + DefaultScheduleTime(item.DueAt));
            UpdateFilterVisuals();
        }
    }

    private async void RowDuePick_Click(object sender, RoutedEventArgs e)
    {
        if (_menuItem is not { } item)
        {
            return;
        }

        _dialogOpen = true;
        CancelDockRetract();
        try
        {
            var (confirmed, dueAt) = await SubtaskDuePrompt.ShowAsync(
                Root.XamlRoot, Root.RequestedTheme, item.DueAt, "设置截止时间", CurrentTintMode());
            if (!confirmed)
            {
                return;
            }

            if (dueAt is { } value)
            {
                ViewModel.ScheduleAt(item, value);
            }
            else
            {
                ViewModel.ClearDue(item);
            }

            UpdateFilterVisuals();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void RowDueClear_Click(object sender, RoutedEventArgs e)
    {
        if (_menuItem is { } item)
        {
            ViewModel.ClearDue(item);
            UpdateFilterVisuals();
        }
    }

    private const string CircleIconPath =
        "M10,10 m-9,0 a9,9 0 1,0 18,0 a9,9 0 1,0 -18,0 Z";

    private const string NoColorIconPath =
        "F0 M10,10 m-9,0 a9,9 0 1,0 18,0 a9,9 0 1,0 -18,0 Z " +
        "M10,10 m-7,0 a7,7 0 1,0 14,0 a7,7 0 1,0 -14,0 Z";

    private void RefreshTagButtonLabels()
    {
        for (var i = 0; i < _tagButtons.Count; i++)
        {
            var label = _settings.GetTagName(i);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_tagButtons[i], label);
            var custom = _settings.GetCustomTagName(i);
            ToolTipService.SetToolTip(_tagButtons[i], custom.Length > 0 ? custom : null);
        }
    }

    private void PopulateTagColorMenu(MenuFlyoutSubItem menu, TodoItem item)
    {
        menu.Items.Clear();
        menu.Items.Add(CreateTagColorItem("无颜色", string.Empty, -1, item, null));

        for (var i = 0; i < MainViewModel.TagColors.Length; i++)
        {
            menu.Items.Add(CreateTagColorItem(
                _settings.GetTagName(i),
                _settings.GetCustomTagName(i),
                i,
                item,
                ParseHexColor(MainViewModel.TagColors[i])));
        }
    }

    private ToggleMenuFlyoutItem CreateTagColorItem(
        string accessibleName,
        string displayText,
        int tagIndex,
        TodoItem item,
        Color? color)
    {
        var entry = new ToggleMenuFlyoutItem
        {
            Text = displayText,
            IsChecked = item.TagIndex == tagIndex,
            Icon = new PathIcon
            {
                Data = ToGeometry(color is null ? NoColorIconPath : CircleIconPath),
                Foreground = color is { } value
                    ? new SolidColorBrush(value)
                    : (Brush)Application.Current.Resources["TextMutedBrush"],
            },
        };

        AutomationProperties.SetName(entry, accessibleName);

        entry.Click += (_, _) =>
        {
            ViewModel.SetTag(item, tagIndex);
            UpdateFilterVisuals();
        };

        return entry;
    }

    private static Color ParseHexColor(string hex) => Color.FromArgb(
        255,
        Convert.ToByte(hex.Substring(1, 2), 16),
        Convert.ToByte(hex.Substring(3, 2), 16),
        Convert.ToByte(hex.Substring(5, 2), 16));

    private static Geometry ToGeometry(string path) =>
        (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), path);

    private void FocusSubtaskAddBox(TodoItem item)
    {
        if (TodoList.ContainerFromItem(item) is not DependencyObject container)
        {
            return;
        }

        if (FindTaggedChild<TextBox>(container, "SubtaskAddBox") is { } box)
        {
            box.Focus(FocusState.Programmatic);
        }
    }

    private void SubtaskAddBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            if (_textComposing)
            {
                return;
            }

            e.Handled = true;
            CommitSubtaskAdd(box, keepOpen: true);
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            box.Text = string.Empty;
            _subtaskAddBox = null;
            if (box.DataContext is TodoItem item)
            {
                item.SubtaskAddActive = false;
            }
        }
    }

    private TextBox? _subtaskAddBox;

    private void SubtaskAddBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
        {
            _subtaskAddBox = box;
            AttachComposerMenu(box);
        }
    }

    private void SubtaskAddBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        // 右键菜单弹出时焦点会被抢走，菜单关掉后还回来；隔一拍再判定，
        // 菜单还开着就等它关闭，期间不提交、不收起。
        ScheduleComposerFocusCheck(
            () => IsWithinOrSelf(FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject, box),
            () => CommitSubtaskAdd(box, keepOpen: false),
            () =>
            {
                if (box.IsLoaded && box.DataContext is TodoItem { SubtaskAddActive: true })
                {
                    box.Focus(FocusState.Programmatic);
                }
            });
    }

    private void SubtaskAddCheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Parent: Grid row } &&
            FindTaggedChild<TextBox>(row, "SubtaskAddBox") is { } box)
        {
            CommitSubtaskAdd(box, keepOpen: false);
        }
    }

    // 窗口失活时 TextBox 不触发 LostFocus，这里补一次离焦提交。
    private void CommitSubtaskAddDraft()
    {
        if (_subtaskAddBox is { } box)
        {
            CommitSubtaskAdd(box, keepOpen: false);
        }
    }

    private void CommitSubtaskAdd(TextBox box, bool keepOpen)
    {
        if (!keepOpen && ReferenceEquals(box, _subtaskAddBox))
        {
            _subtaskAddBox = null;
        }

        // 模板已经回收/收起时别再往旧条目上加。
        if (!box.IsLoaded || box.DataContext is not TodoItem item)
        {
            return;
        }

        var title = box.Text.Trim();
        if (title.Length > 0)
        {
            ViewModel.AddSubtask(item, title);
            box.Text = string.Empty;
        }

        // 回车提交后保留输入框，方便连续加；离焦或 Esc 才收起。
        item.SubtaskAddActive = keepOpen;
        if (keepOpen)
        {
            box.Focus(FocusState.Programmatic);
        }
    }

    private void RowEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_menuItem is { } item)
        {
            _ = EditItemAsync(item);
        }
    }

    private void RowSnooze_Click(object sender, RoutedEventArgs e)
    {
        if (_menuItem is { } item)
        {
            ViewModel.Snooze(item, 10);
            ShowBalloon("已推迟", $"{item.Title}：10 分钟后再提醒");
        }
    }

    private void RowDelete_Click(object sender, RoutedEventArgs e)
    {
        _ = ConfirmDeleteAsync();
    }

    private async Task ConfirmDeleteAsync()
    {
        if (_menuItem is not { } item)
        {
            return;
        }

        var dialog = CreateCardDialog("删除待办", $"确定删除「{item.Title}」吗？", "删除");

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        ViewModel.Delete(item);
        UpdateFilterVisuals();
        ShowUndo($"已删除「{item.Title}」", new List<TodoItem> { item });
    }

    private async Task EditItemAsync(TodoItem item, AppWindow? owner = null)
    {
        var dialog = new TodoEditorDialog(_settings, item);

        var saved = await ShowEditorAsync(dialog, owner);
        RestoreOwnerFocus(owner);
        if (saved)
        {
            ViewModel.ApplyEdit(
                item, dialog.TodoTitle, dialog.DueAt, dialog.Recurrence, dialog.IsImportant, dialog.TagIndex, dialog.TodoNote);
            ViewModel.ApplySubtasks(item, dialog.Subtasks);
            UpdateFilterVisuals();
        }
    }

    /// <summary>
    /// 编辑窗是独立窗口，关闭后系统会把焦点还给主窗口；从日历打开时要把日历重新提到最前，
    /// 否则日历会被主窗口盖住，看起来像「保存后日历消失了」。
    /// </summary>
    private static void RestoreOwnerFocus(AppWindow? owner)
    {
        if (owner is null)
        {
            return;
        }

        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(owner.Id);
        if (hwnd != IntPtr.Zero && NativeMethods.IsWindowVisible(hwnd))
        {
            NativeMethods.SetForegroundWindow(hwnd);
        }
    }

    private void TodoList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        _calendarHostWindow?.Calendar.EndExternalDrag();
        var ordered = ViewModel.Items.ToList();
        DispatcherQueue.TryEnqueue(() => ViewModel.PersistReorder(ordered));
    }
}
