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
    private void CreateTray()
    {
        _tray = new TrayIcon(_hwnd);
        _tray.ShowRequested += ShowFromTray;
        _tray.MenuRequested += ShowTrayMenu;
        _tray.Add();
    }

    /// <summary>托盘右键菜单：用 WinUI MenuFlyout，跟窗口同一套暗色圆角菜单样式。</summary>
    private bool _trayMenuOpen;

    /// <summary>
    /// 托盘右键菜单：优先 WinUI MenuFlyout；它一旦抛异常（窗口未激活等），
    /// 直接退回深色 Win32 菜单，保证菜单一定能出来，顺便把异常记进日志。
    /// </summary>
    public void ShowTrayMenu()
    {
        // 托盘消息是在窗口过程里来的，直接在里面弹 XAML 菜单会重入消息循环（激活、弹窗都要
        // 等 UI 线程，而 UI 线程正卡在这个回调里），表现就是右键一下整个应用不动。
        // 先切回正常的 dispatcher 回调，再弹菜单。
        _dispatcher.TryEnqueue(ShowTrayMenuCore);
    }

    private void ShowTrayMenuCore()
    {
        // 内容树还没挂上 XamlRoot 时（启动瞬间）飞来托盘消息，直接跳过。
        if (Root.XamlRoot is null)
        {
            return;
        }

        _trayMenuOpen = true;
        try
        {
            Activate();
            NativeMethods.SetForegroundWindow(_hwnd);
            ShowWinUiTrayMenu();
            return;
        }
        catch (Exception exception)
        {
            _trayMenuOpen = false;
            LogMenuFailure(exception);
        }

        ShowNativeTrayMenu();
    }

    private void ShowWinUiTrayMenu()
    {
        NativeMethods.GetCursorPos(out var cursor);
        NativeMethods.ScreenToClient(_hwnd, ref cursor);
        var scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;

        var flyout = new MenuFlyout();

        var showItem = new MenuFlyoutItem
        {
            Text = "显示待办",
            Icon = new FontIcon { Glyph = "\uE7B3" },
        };
        showItem.Click += (_, _) => ShowFromTray();

        var calendarItem = new MenuFlyoutItem
        {
            Text = "显示日历",
            Icon = new FontIcon { Glyph = "\uE787" },
        };
        calendarItem.Click += (_, _) => CalendarButton_Click(this, new RoutedEventArgs());

        var settingsItem = new MenuFlyoutItem
        {
            Text = "设置",
            Icon = new FontIcon { Glyph = "\uE713" },
        };
        settingsItem.Click += (_, _) => SettingsButton_Click(this, new RoutedEventArgs());

        var exitItem = new MenuFlyoutItem
        {
            Text = "退出",
            Icon = new FontIcon { Glyph = "\uE7E8" },
        };
        exitItem.Click += (_, _) => ExitApp();

        flyout.Items.Add(showItem);
        flyout.Items.Add(calendarItem);
        flyout.Items.Add(settingsItem);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(exitItem);

        flyout.Closed += (_, _) =>
        {
            _trayMenuOpen = false;
        };

        flyout.ShowAt(Root, new FlyoutShowOptions
        {
            Position = new Windows.Foundation.Point(cursor.X / scale, cursor.Y / scale),
        });
    }

    /// <summary>WinUI 菜单不可用时的兜底：系统深色弹出菜单（EnableDarkWin32Menus 已把主题设为 ForceDark）。</summary>
    private void ShowNativeTrayMenu()
    {
        NativeMethods.GetCursorPos(out var point);

        var menu = NativeMethods.CreatePopupMenu();
        NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, 1, "显示待办");
        NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, 2, "显示日历");
        NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, 3, "设置");
        NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, 0, null);
        NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, 4, "退出");

        var command = NativeMethods.TrackPopupMenuEx(
            menu,
            NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_NONOTIFY,
            point.X, point.Y, _hwnd, IntPtr.Zero);

        NativeMethods.DestroyMenu(menu);
        NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);

        switch (command)
        {
            case 1:
                ShowFromTray();
                break;
            case 2:
                CalendarButton_Click(this, new RoutedEventArgs());
                break;
            case 3:
                SettingsButton_Click(this, new RoutedEventArgs());
                break;
            case 4:
                ExitApp();
                break;
        }
    }

    private static void LogMenuFailure(Exception exception)
    {
        try
        {
            File.AppendAllText(AppPaths.LogPath, $"[{DateTime.Now:O}] tray menu failed: {exception}\r\n");
        }
        catch
        {
            // 日志失败忽略
        }
    }

    private List<TodoItem>? _undoItems;
    private List<MainViewModel.OverdueSnapshot>? _undoPostpone;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _undoTimer;

    private void ShowUndo(string message, List<TodoItem> items)
    {
        _undoPostpone = null;
        _undoItems = items;
        StartUndoBar(message);
    }

    private void ShowUndoPostpone(string message, List<MainViewModel.OverdueSnapshot> snapshot)
    {
        _undoItems = null;
        _undoPostpone = snapshot;
        StartUndoBar(message);
    }

    private void StartUndoBar(string message)
    {
        UndoText.Text = message;
        UndoBar.Visibility = Visibility.Visible;

        _undoTimer ??= _dispatcher.CreateTimer();
        _undoTimer.Stop();
        _undoTimer.Interval = TimeSpan.FromSeconds(6);

        void OnTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
        {
            sender.Tick -= OnTick;
            HideUndo();
        }

        _undoTimer.Tick += OnTick;
        _undoTimer.Start();
    }

    private void HideUndo()
    {
        _undoTimer?.Stop();
        _undoItems = null;
        _undoPostpone = null;
        UndoBar.Visibility = Visibility.Collapsed;
    }

    /// <summary>启动后静默检查一次 GitHub Release，有新版本只提示一次。</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _updateCheckTimer;

    /// <summary>
    /// 启动后查一次，之后每天查一次。只在发现新版本、且这次还没提醒过时弹气泡；
    /// 真正安装仍由用户在设置里点「检查更新」触发。
    /// </summary>
    private void ScheduleUpdateCheck()
    {
        // 启动延迟 8 秒，别和开屏动画、首次同步抢时间。
        var first = _dispatcher.CreateTimer();
        first.Interval = TimeSpan.FromSeconds(8);
        first.IsRepeating = false;
        first.Tick += (_, _) =>
        {
            first.Stop();
            _ = CheckUpdateQuietAsync();
        };
        first.Start();

        // 之后每 24 小时一次；进程活过夜也能收到。
        _updateCheckTimer ??= _dispatcher.CreateTimer();
        _updateCheckTimer.Stop();
        _updateCheckTimer.Interval = TimeSpan.FromHours(24);
        _updateCheckTimer.IsRepeating = true;
        _updateCheckTimer.Tick += (_, _) => _ = CheckUpdateQuietAsync();
        _updateCheckTimer.Start();
    }

    private async Task CheckUpdateQuietAsync()
    {
        try
        {
            // 同一天只查一次：频繁重启也不该反复打网络。
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            if (string.Equals(_settings.Current.LastUpdateCheckDate, today, StringComparison.Ordinal))
            {
                return;
            }

            _settings.Current.LastUpdateCheckDate = today;
            _settings.Save();

            var result = await UpdateService.CheckAsync(ToDooo.Helpers.AppInfo.Version);
            if (!result.HasUpdate || result.Version is not { } version)
            {
                return;
            }

            // 记下待更新版本，下次启动还能提示；同一版本只弹一次，别每天烦人。
            _settings.Current.PendingUpdateVersion = version;
            if (version == _settings.Current.LastNotifiedVersion)
            {
                _settings.Save();
                return;
            }

            _settings.Current.LastNotifiedVersion = version;
            _settings.Save();
            ShowBalloon("发现新版本", $"ToDooo {version} 已发布，打开设置点「检查更新」即可直接安装");
        }
        catch
        {
            // 静默检查失败不影响使用
        }
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_undoItems is { Count: > 0 } items)
        {
            ViewModel.Restore(items);
            UpdateFilterVisuals();
            UpdateTagVisuals();
        }
        else if (_undoPostpone is { Count: > 0 } snapshot)
        {
            ViewModel.RestoreDueDates(snapshot);
            UpdateFilterVisuals();
            UpdateTagVisuals();
        }

        HideUndo();
    }

    public async Task ExportDataAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"ToDooo-{DateTime.Now:yyyyMMdd}",
            };
            picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);

            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            var items = ViewModel.AllItems.Select(item => new
            {
                item.Title,
                item.Note,
                item.IsDone,
                item.IsImportant,
                DueAt = item.DueAt?.ToString("yyyy-MM-dd'T'HH:mm:ss"),
                Recurrence = item.Recurrence.ToString(),
                item.TagIndex,
                item.SortOrder,
                CreatedAt = item.CreatedAt.ToString("yyyy-MM-dd'T'HH:mm:ss"),
                CompletedAt = item.CompletedAt?.ToString("yyyy-MM-dd'T'HH:mm:ss"),
                Subtasks = item.Subtasks.Select(subtask => new
                {
                    subtask.Title,
                    subtask.Note,
                    subtask.IsDone,
                    DueAt = subtask.DueAt?.ToString("yyyy-MM-dd'T'HH:mm:ss"),
                    subtask.SortOrder,
                }),
            });

            var json = System.Text.Json.JsonSerializer.Serialize(
                items, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await Windows.Storage.FileIO.WriteTextAsync(file, json);
            ShowBalloon("已导出", file.Name);
        }
        catch
        {
            ShowBalloon("导出失败", "换个位置再试试");
        }
    }

    private sealed class ImportedSubtaskDto
    {
        public string? Title { get; set; }

        public string? Note { get; set; }

        public bool IsDone { get; set; }

        public string? DueAt { get; set; }

        public double SortOrder { get; set; }
    }

    private sealed class ImportedTodoDto
    {
        public string? Title { get; set; }

        public string? Note { get; set; }

        public bool IsDone { get; set; }

        public bool IsImportant { get; set; }

        public string? DueAt { get; set; }

        public string? Recurrence { get; set; }

        public int TagIndex { get; set; } = -1;

        public double SortOrder { get; set; }

        public string? CreatedAt { get; set; }

        public string? CompletedAt { get; set; }

        public List<ImportedSubtaskDto>? Subtasks { get; set; }
    }

    public async Task ImportDataAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeFilter.Add(".json");
            var ownerHwnd = _settingsWindow is not null
                ? WinRT.Interop.WindowNative.GetWindowHandle(_settingsWindow)
                : _hwnd;
            WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerHwnd);

            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            var json = await Windows.Storage.FileIO.ReadTextAsync(file);
            var rawItems = System.Text.Json.JsonSerializer.Deserialize<List<ImportedTodoDto>>(
                json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (rawItems is null || rawItems.Count == 0)
            {
                ShowBalloon("导入为空", "文件中没有可导入的待办");
                return;
            }

            var imported = new List<TodoItem>(rawItems.Count);
            foreach (var dto in rawItems)
            {
                var title = (dto.Title ?? string.Empty).Trim();
                if (title.Length == 0)
                {
                    continue;
                }

                _ = Enum.TryParse<TodoRecurrence>(dto.Recurrence, ignoreCase: true, out var recurrence);
                var todo = new TodoItem
                {
                    Title = title,
                    Note = (dto.Note ?? string.Empty).Trim(),
                    IsDone = dto.IsDone,
                    IsImportant = dto.IsImportant,
                    DueAt = DateTime.TryParse(dto.DueAt, out var due) ? due : null,
                    Recurrence = recurrence,
                    TagIndex = dto.TagIndex >= -1 && dto.TagIndex < MainViewModel.TagColors.Length ? dto.TagIndex : -1,
                    SortOrder = dto.SortOrder,
                    CreatedAt = DateTime.TryParse(dto.CreatedAt, out var created) ? created : DateTime.Now,
                    CompletedAt = DateTime.TryParse(dto.CompletedAt, out var completed) ? completed : null,
                };

                if (dto.Subtasks is { Count: > 0 })
                {
                    foreach (var subDto in dto.Subtasks)
                    {
                        var subTitle = (subDto.Title ?? string.Empty).Trim();
                        if (subTitle.Length == 0)
                        {
                            continue;
                        }

                        todo.Subtasks.Add(new SubtaskItem
                        {
                            Title = subTitle,
                            Note = (subDto.Note ?? string.Empty).Trim(),
                            IsDone = subDto.IsDone,
                            DueAt = DateTime.TryParse(subDto.DueAt, out var subDue) ? subDue : null,
                            SortOrder = subDto.SortOrder,
                        });
                    }
                }

                imported.Add(todo);
            }

            var count = ViewModel.ImportItems(imported);
            UpdateFilterVisuals();
            UpdateTagVisuals();
            ShowBalloon("已导入", $"共导入 {count} 条待办");
        }
        catch
        {
            ShowBalloon("导入失败", "请确认 JSON 文件格式正确");
        }
    }

    private void SnoozeReminder(long id)
    {
        var item = ViewModel.AllItems.FirstOrDefault(candidate => candidate.Id == id);
        if (item is null || item.IsDone)
        {
            return;
        }

        ViewModel.Snooze(item, 10);
        ShowBalloon("已推迟", $"{item.Title}：10 分钟后再提醒");
    }

    private void CompleteFromNotification(long id)
    {
        var item = ViewModel.AllItems.FirstOrDefault(candidate => candidate.Id == id);
        if (item is null || item.IsDone)
        {
            return;
        }

        ViewModel.SetDone(item, true);
        UpdateFilterVisuals();
    }

    private void CompleteSubtaskFromNotification(long todoId, long subtaskId)
    {
        var item = ViewModel.AllItems.FirstOrDefault(candidate => candidate.Id == todoId);
        var subtask = item?.Subtasks.FirstOrDefault(candidate => candidate.Id == subtaskId);
        if (item is null || subtask is null || subtask.IsDone)
        {
            return;
        }

        ViewModel.SetSubtaskDone(item, subtask, true);
        UpdateFilterVisuals();
    }
}
