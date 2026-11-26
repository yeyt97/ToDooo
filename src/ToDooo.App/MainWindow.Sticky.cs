using Microsoft.UI.Xaml;
using ToDooo.Interop;
using ToDooo.Models;
using ToDooo.Services;
using ToDooo.Views;
using Windows.Foundation;

namespace ToDooo;

public sealed partial class MainWindow
{
    private readonly StickyNoteStore _stickyStore = new();
    private readonly Dictionary<string, StickyNoteWindow> _openStickyWindows = new(StringComparer.OrdinalIgnoreCase);
    private StickyFlyoutWindow? _stickyPopup;

    private void RestoreVisibleStickyNotes()
    {
        if (!_settings.Current.StickyRestoreOnStartup)
        {
            return;
        }

        foreach (var note in _stickyStore.Notes)
        {
            if (note.Visible)
            {
                try
                {
                    OpenStickyNote(note, focus: false);
                }
                catch (Exception ex)
                {
                    var logPath = System.IO.Path.Combine(AppPaths.Root, "error.log");
                    System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:O}] RestoreSticky {note.Id}: {ex}\r\n");
                }
            }
        }
    }

    private StickyFlyoutWindow EnsureStickyPopup()
    {
        if (_stickyPopup is not null)
        {
            return _stickyPopup;
        }

        _stickyPopup = new StickyFlyoutWindow(
            _stickyStore,
            _settings,
            isNoteOpen: id => _openStickyWindows.ContainsKey(id),
            onCreateNew: colorKey => CreateAndOpenStickyNote(nearNote: null, activateWindow: false, overrideColor: colorKey),
            onSetNoteVisible: (note, visible) =>
            {
                if (visible)
                {
                    OpenStickyNote(note, focus: false, activateWindow: false);
                }
                else if (_openStickyWindows.TryGetValue(note.Id, out var existing))
                {
                    note.Visible = false;
                    _stickyStore.SaveMeta();
                    existing.Close();
                }
            },
            onBringToFront: note =>
            {
                if (_openStickyWindows.TryGetValue(note.Id, out var existing))
                {
                    existing.BringToFrontNoActivate();
                }
                else
                {
                    OpenStickyNote(note, focus: false, activateWindow: false);
                }
            },
            onDelete: DeleteStickyNote,
            onRestoreDeleted: RestoreDeletedStickyNote,
            onColorChanged: note =>
            {
                if (_openStickyWindows.TryGetValue(note.Id, out var openWin))
                {
                    openWin.RefreshTheme();
                }
            },
            onTogglePin: note =>
            {
                note.Pinned = !note.Pinned;
                _stickyStore.SaveMeta();
                if (_openStickyWindows.TryGetValue(note.Id, out var openWin))
                {
                    openWin.RefreshPinState();
                }
            },
            onConvertToTodo: note =>
            {
                var title = note.Title;
                if (!string.IsNullOrWhiteSpace(title) && title != "空白便签")
                {
                    ViewModel.Add(title);
                }
            });

        _stickyPopup.Closed += (_, _) => _stickyPopup = null;
        return _stickyPopup;
    }

    private void StickyButton_Click(object sender, RoutedEventArgs e)
    {
        var popup = EnsureStickyPopup();
        if (popup.IsPopupVisible)
        {
            popup.HidePopup();
            return;
        }

        ShowStickyPopupBelowButton();
    }

    private void ShowStickyPopupBelowButton()
    {
        var popup = EnsureStickyPopup();
        if (!NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            return;
        }

        var scale = Math.Max(1.0, NativeMethods.GetDpiForWindow(_hwnd) / 96.0);
        var btnPos = StickyButton.TransformToVisual(Root).TransformPoint(new Point(StickyButton.ActualWidth, StickyButton.ActualHeight));
        var rightX = rect.Left + (int)Math.Round(btnPos.X * scale);
        var bottomY = rect.Top + (int)Math.Round(btnPos.Y * scale);
        popup.ShowBelowScreenPoint(rightX, bottomY);
    }

    private void ShowStickyPopupAboveTray(int screenX, int screenY)
    {
        var popup = EnsureStickyPopup();
        popup.ShowAboveScreenPoint(screenX, screenY);
    }

    private void OpenStickyManager() => ShowStickyPopupBelowButton();

    private StickyNote CreateAndOpenStickyNote(StickyNote? nearNote, bool activateWindow = true, string? overrideColor = null)
    {
        var color = overrideColor ?? nearNote?.Color ?? _settings.Current.StickyDefaultColor;
        var note = _stickyStore.Create(color);
        note.FontSize = Math.Clamp(_settings.Current.StickyDefaultFontSize, 11.0, 20.0);
        note.Pinned = _settings.Current.StickyDefaultPinned;
        if (nearNote?.X is int nx && nearNote.Y is int ny)
        {
            note.X = nx + 36;
            note.Y = ny + 36;
        }

        _stickyStore.SaveMeta(notify: false);
        OpenStickyNote(note, focus: activateWindow, activateWindow: activateWindow);
        return note;
    }

    private void OpenStickyNote(StickyNote note, bool focus, bool activateWindow = true)
    {
        if (_openStickyWindows.TryGetValue(note.Id, out var existing))
        {
            if (focus)
            {
                existing.FocusEditor();
            }
            else if (activateWindow)
            {
                existing.Activate();
            }

            return;
        }

        note.Visible = true;
        _stickyStore.SaveMeta();

        var win = new StickyNoteWindow(
            note,
            _stickyStore,
            _settings,
            onCreateNew: source => CreateAndOpenStickyNote(source),
            onOpenManager: OpenStickyManager,
            onConvertToTodo: (title, _) => ViewModel.Add(title),
            onDelete: DeleteStickyNote,
            onClosed: id => _openStickyWindows.Remove(id));

        _openStickyWindows[note.Id] = win;
        if (activateWindow)
        {
            win.Activate();
            if (focus)
            {
                win.FocusEditor();
            }
        }
        else
        {
            win.AppWindow.Show(activateWindow: false);
        }
    }

    private StickyNote RestoreDeletedStickyNote(StickyNote snapshot, bool reopen)
    {
        var note = _stickyStore.Create(snapshot.Color);
        note.Content = snapshot.Content;
        note.Pinned = snapshot.Pinned;
        note.FontSize = snapshot.FontSize;
        note.X = snapshot.X;
        note.Y = snapshot.Y;
        note.Width = snapshot.Width;
        note.Height = snapshot.Height;
        note.Visible = reopen;
        _stickyStore.SaveContent(note);
        _stickyStore.SaveMeta();
        if (reopen)
        {
            OpenStickyNote(note, focus: false, activateWindow: false);
        }

        return note;
    }

    private void RowToSticky_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TodoItem item })
        {
            return;
        }

        var lines = new List<string> { item.Title };
        if (!string.IsNullOrWhiteSpace(item.Note))
        {
            lines.Add(item.Note.Trim());
        }

        foreach (var sub in item.Subtasks)
        {
            var mark = sub.IsDone ? "☑" : "☐";
            lines.Add($"{mark} {sub.Title}");
        }

        var color = MapTodoTagToStickyColor(item.TagIndex);
        var note = _stickyStore.Create(color);
        note.Content = string.Join(Environment.NewLine, lines);
        note.FontSize = Math.Clamp(_settings.Current.StickyDefaultFontSize, 11.0, 20.0);
        note.Pinned = _settings.Current.StickyDefaultPinned;
        _stickyStore.SaveContent(note);
        _stickyStore.SaveMeta();
        OpenStickyNote(note, focus: true, activateWindow: true);
    }

    private string MapTodoTagToStickyColor(int tagIndex) => tagIndex switch
    {
        1 => "pink",
        2 => "yellow",
        3 => "green",
        4 => "blue",
        5 => "purple",
        _ => _settings.Current.StickyDefaultColor,
    };

    private void DeleteStickyNote(StickyNote note)
    {
        if (_openStickyWindows.TryGetValue(note.Id, out var win))
        {
            _openStickyWindows.Remove(note.Id);
            win.Close();
        }

        _stickyStore.Delete(note.Id);
    }

    private void RefreshStickyThemes()
    {
        _stickyPopup?.RefreshTheme();
        foreach (var win in _openStickyWindows.Values)
        {
            win.RefreshTheme();
        }
    }

    private void CloseAllStickyWindows()
    {
        _stickyPopup?.Close();
        _stickyPopup = null;
        foreach (var win in _openStickyWindows.Values.ToList())
        {
            win.Close();
        }

        _openStickyWindows.Clear();
    }
}
