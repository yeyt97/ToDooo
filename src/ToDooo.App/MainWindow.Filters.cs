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
    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        if (SearchBar.Visibility != Visibility.Visible)
        {
            OpenSearchBar();
        }
    }

    private void OpenSearchBar()
    {
        SearchBar.Visibility = Visibility.Visible;
        SearchButton.IsEnabled = false;
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void CloseSearchBar()
    {
        SearchBox.Text = string.Empty;
        ViewModel.SetSearch(string.Empty);
        SearchBar.Visibility = Visibility.Collapsed;
        SearchButton.IsEnabled = true;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ViewModel.SetSearch(SearchBox.Text);
        ClearSearchButton.Visibility = SearchBox.Text.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e) => CloseSearchBar();

    private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // 没内容就收起搜索条，不占位置。
        if (SearchBox.Text.Length == 0 && SearchBar.Visibility == Visibility.Visible)
        {
            CloseSearchBar();
        }
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CloseSearchBar();
        }
    }

    private void NewTodoAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        FocusAddBox();
    }

    private void SearchAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OpenSearchBar();
    }

    private void FilterAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel.SetFilter(sender.Key switch
        {
            VirtualKey.Number1 => TodoFilter.All,
            VirtualKey.Number2 => TodoFilter.Active,
            VirtualKey.Number3 => TodoFilter.Important,
            VirtualKey.Number4 => TodoFilter.Done,
            _ => TodoFilter.All,
        });
        UpdateFilterVisuals();
    }

    private void BuildTagDots()
    {
        var style = (Style)Application.Current.Resources["TagDotButtonStyle"];

        for (var i = 0; i < MainViewModel.TagColors.Length; i++)
        {
            var hex = MainViewModel.TagColors[i];
            var color = Color.FromArgb(
                255,
                Convert.ToByte(hex.Substring(1, 2), 16),
                Convert.ToByte(hex.Substring(3, 2), 16),
                Convert.ToByte(hex.Substring(5, 2), 16));
            AddTagButton(style, i, color, _settings.GetTagName(i));
        }

        // 选中某个颜色后，末尾才出现的“清除颜色”按钮（圆圈 + 叉）。
        var clearIcon = new Grid
        {
            Width = 18,
            Height = 18,
        };
        clearIcon.Children.Add(new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["TextMutedBrush"],
        });
        clearIcon.Children.Add(new FontIcon
        {
            Glyph = "\uE711",
            FontSize = 7,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
        });

        _tagClearButton = new Button
        {
            Style = style,
            Content = clearIcon,
            Visibility = Visibility.Collapsed,
        };
        _tagClearButton.Click += (_, _) =>
        {
            ViewModel.SelectTag(-1);
            UpdateTagVisuals();
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_tagClearButton, "清除颜色筛选");
        TagPanel.Children.Add(_tagClearButton);
        RefreshTagButtonLabels();
    }

    private void AddTagButton(Style style, int index, Color color, string name)
    {
        // 和编辑弹窗同一套：18px 圆点，选中时圆点里出现对勾。
        var dot = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(color),
        };

        var luminance = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B;
        var check = new FontIcon
        {
            Glyph = "",
            FontSize = 11,
            Foreground = new SolidColorBrush(luminance > 150
                ? Color.FromArgb(255, 0x26, 0x2B, 0x33)
                : Color.FromArgb(255, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };

        var content = new Grid();
        content.Children.Add(dot);
        content.Children.Add(check);

        var button = new Button
        {
            Style = style,
            Content = content,
            Tag = index,
            Background = (Brush)Application.Current.Resources["TransparentBrush"],
        };
        button.Click += TagDot_Click;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
        _tagButtons.Add(button);
        _tagChecks.Add(check);
        TagPanel.Children.Add(button);
    }

    private void TagDot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int index })
        {
            // 再点一次已选中的颜色即取消筛选。
            ViewModel.SelectTag(ViewModel.SelectedTag == index ? -1 : index);
            UpdateTagVisuals();
        }
    }

    private void UpdateTagVisuals()
    {
        // 选中只显示圆点里的对勾，不加灰底。
        var transparent = (Brush)Application.Current.Resources["TransparentBrush"];
        for (var i = 0; i < _tagButtons.Count; i++)
        {
            _tagChecks[i].Visibility = ViewModel.SelectedTag == i ? Visibility.Visible : Visibility.Collapsed;
            _tagButtons[i].Background = transparent;
        }

        if (_tagClearButton is not null)
        {
            _tagClearButton.Visibility = ViewModel.SelectedTag >= 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void BuildSmartViewChips()
    {
        SmartViewPanel.Children.Clear();
        _smartViewButtons.Clear();

        foreach (var setting in _settings.Current.SmartViews)
        {
            if (!setting.Enabled || !Enum.IsDefined(typeof(SmartView), setting.View))
            {
                continue;
            }

            var view = (SmartView)setting.View;
            var button = new Button
            {
                Content = SmartViewInfo.Title(view),
                Tag = view,
                Style = (Style)Application.Current.Resources["SmartChipStyle"],
            };
            button.Click += SmartView_Click;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                button, $"视图 {SmartViewInfo.Title(view)}");
            _smartViewButtons.Add(button);
            SmartViewPanel.Children.Add(button);
        }

        SmartViewPanel.Visibility = _smartViewButtons.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateSmartViewVisuals();
    }

    private void SmartView_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SmartView view })
        {
            ViewModel.ToggleSmartView(view);
            UpdateFilterVisuals();
        }
    }

    private void UpdateSmartViewVisuals()
    {
        foreach (var button in _smartViewButtons)
        {
            var active = button.Tag is SmartView view && (int)view == ViewModel.SelectedSmartView;
            button.Background = active
                ? PaletteBrush("SegmentSelectedBrush")
                : PaletteBrush("TransparentBrush");
            button.Foreground = active
                ? PaletteBrush("TextPrimaryBrush")
                : PaletteBrush("TextMutedBrush");
        }
    }

    private void UpdateFilterVisuals()
    {
        SetStatVisual(AllFilterButton, ViewModel.Filter == TodoFilter.All);
        SetStatVisual(ActiveStat, ViewModel.Filter == TodoFilter.Active);
        SetStatVisual(ImportantStat, ViewModel.Filter == TodoFilter.Important);
        SetStatVisual(DoneStat, ViewModel.Filter == TodoFilter.Done);

        // 批量删除只在「已完成」视图里出现，避免在其他视图误触。
        ClearCompletedButton.Visibility = ViewModel.Filter == TodoFilter.Done
            ? Visibility.Visible
            : Visibility.Collapsed;

        // 一键顺延仅在「已逾期」智能视图且存在逾期条目时出现。
        PostponeOverdueButton.Visibility =
            ViewModel.Filter != TodoFilter.Done &&
            ViewModel.SelectedSmartView == (int)SmartView.Overdue &&
            ViewModel.HasOverdue
                ? Visibility.Visible
                : Visibility.Collapsed;

        // 切换分段筛选时，智能视图的选中态也要跟着刷。
        UpdateSmartViewVisuals();
    }

    private void PostponeOverdue_Click(object sender, RoutedEventArgs e)
    {
        var count = ViewModel.RescheduleOverdueToToday(out var snapshot);
        UpdateFilterVisuals();
        if (count > 0)
        {
            ShowUndoPostpone($"已将 {count} 项逾期顺延到今天", snapshot);
        }
    }

    // 分段筛选的选中态是一块中性高亮，而不是强调色或透明度差异。
    private void SetStatVisual(Button button, bool active)
    {
        button.Background = active ? PaletteBrush("SegmentSelectedBrush") : PaletteBrush("TransparentBrush");
        button.Foreground = PaletteBrush(active ? "TextPrimaryBrush" : "TextMutedBrush");
    }

    private void AllFilter_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearSmartView();
        ViewModel.SetFilter(TodoFilter.All);
        UpdateFilterVisuals();
    }

    private void ActiveFilter_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearSmartView();
        ViewModel.SetFilter(TodoFilter.Active);
        UpdateFilterVisuals();
    }

    private void ImportantFilter_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearSmartView();
        ViewModel.SetFilter(TodoFilter.Important);
        UpdateFilterVisuals();
    }

    private void DoneFilter_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearSmartView();
        ViewModel.SetFilter(TodoFilter.Done);
        UpdateFilterVisuals();
    }
}
