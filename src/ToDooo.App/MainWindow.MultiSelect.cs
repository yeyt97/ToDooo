using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ToDooo.Helpers;
using ToDooo.Models;
using ToDooo.ViewModels;
using Windows.System;
using Windows.UI.Core;

namespace ToDooo;

/// <summary>
/// 待办多选批量操作：Ctrl 点击多选、Shift 点击连选，底部浮出批量操作栏
/// （批量完成 / 顺延明天 / 改颜色标签 / 批量删除）。
/// </summary>
public sealed partial class MainWindow
{
    private readonly HashSet<long> _selectedTodoIds = [];
    private long? _lastSelectedTodoId;

    private bool HasMultiSelection => _selectedTodoIds.Count > 0;

    private void TodoCard_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border { DataContext: TodoItem item } card)
        {
            return;
        }

        if (!WindowDragHelper.IsPrimaryPointerPressed(card, e))
        {
            return;
        }

        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(CoreVirtualKeyStates.Down);
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(CoreVirtualKeyStates.Down);

        if (!ctrl && !shift && !HasMultiSelection)
        {
            return;
        }

        e.Handled = true;

        if (shift && _lastSelectedTodoId is long anchorId)
        {
            var items = ViewModel.Items;
            var a = -1;
            var b = -1;
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].Id == anchorId) a = i;
                if (items[i].Id == item.Id) b = i;
            }

            if (a >= 0 && b >= 0)
            {
                var start = Math.Min(a, b);
                var end = Math.Max(a, b);
                for (var i = start; i <= end; i++)
                {
                    _selectedTodoIds.Add(items[i].Id);
                }
            }
            else
            {
                _selectedTodoIds.Add(item.Id);
            }
        }
        else
        {
            if (!_selectedTodoIds.Remove(item.Id))
            {
                _selectedTodoIds.Add(item.Id);
            }

            _lastSelectedTodoId = item.Id;
        }

        UpdateMultiSelectionVisuals();
    }

    private void UpdateMultiSelectionVisuals()
    {
        var accentBrush = Application.Current.Resources["AccentBrush"] as SolidColorBrush;
        var selectedStroke = accentBrush is not null
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xCC, accentBrush.Color.R, accentBrush.Color.G, accentBrush.Color.B))
            : (Brush)Application.Current.Resources["AccentBrush"];
        var normalStroke = (Brush)Application.Current.Resources["ItemCardStrokeBrush"];

        for (var i = 0; i < TodoList.Items.Count; i++)
        {
            if (TodoList.ContainerFromIndex(i) is not ListViewItem container)
            {
                continue;
            }

            if (container.ContentTemplateRoot is Border card && container.Content is TodoItem item)
            {
                var selected = _selectedTodoIds.Contains(item.Id);
                card.BorderBrush = selected ? selectedStroke : normalStroke;
                card.BorderThickness = new Thickness(selected ? 2 : 1);
            }
        }

        if (_selectedTodoIds.Count > 0)
        {
            BatchCountText.Text = $"已选 {_selectedTodoIds.Count} 项";
            BatchBar.Visibility = Visibility.Visible;
        }
        else
        {
            BatchBar.Visibility = Visibility.Collapsed;
        }
    }

    private void ClearMultiSelection()
    {
        if (_selectedTodoIds.Count == 0)
        {
            return;
        }

        _selectedTodoIds.Clear();
        _lastSelectedTodoId = null;
        UpdateMultiSelectionVisuals();
    }

    private List<TodoItem> GetSelectedTodos()
        => ViewModel.Items.Where(t => _selectedTodoIds.Contains(t.Id)).ToList();

    private void BatchComplete_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in GetSelectedTodos())
        {
            if (!item.IsDone)
            {
                ViewModel.SetDone(item, true);
            }
        }

        ClearMultiSelection();
        UpdateFilterVisuals();
    }

    private void BatchTomorrow_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in GetSelectedTodos())
        {
            ViewModel.ScheduleAt(item, DateTime.Today.AddDays(1) + DefaultScheduleTime(item.DueAt));
        }

        ClearMultiSelection();
        UpdateFilterVisuals();
    }

    private void BatchColorFlyout_Opening(object? sender, object e)
    {
        BatchColorPanel.Children.Clear();
        var style = (Style)Application.Current.Resources["TagDotButtonStyle"];

        for (var i = 0; i < MainViewModel.TagColors.Length; i++)
        {
            var idx = i;
            var dot = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = idx == 0
                    ? new SolidColorBrush(Colors.Transparent)
                    : Bind.TagBrush(idx),
                BorderBrush = idx == 0 ? (Brush)Application.Current.Resources["TextMutedBrush"] : null,
                BorderThickness = idx == 0 ? new Thickness(1.2) : new Thickness(0),
            };
            var btn = new Button { Style = style, Content = dot };
            ToolTipService.SetToolTip(btn, idx == 0 ? "清除颜色" : $"标签 {idx}");
            btn.Click += (_, _) =>
            {
                foreach (var item in GetSelectedTodos())
                {
                    ViewModel.SetTag(item, idx);
                }

                BatchColorButton.Flyout?.Hide();
                ClearMultiSelection();
                UpdateFilterVisuals();
            };
            BatchColorPanel.Children.Add(btn);
        }
    }

    private void BatchDelete_Click(object sender, RoutedEventArgs e)
    {
        var targets = GetSelectedTodos();
        if (targets.Count == 0)
        {
            return;
        }

        foreach (var item in targets)
        {
            ViewModel.Delete(item);
        }

        ClearMultiSelection();
        UpdateFilterVisuals();
    }

    private void BatchClear_Click(object sender, RoutedEventArgs e) => ClearMultiSelection();
}
