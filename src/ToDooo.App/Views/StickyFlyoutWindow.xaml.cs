using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using ToDooo.Interop;
using ToDooo.Models;
using ToDooo.Services;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI;

namespace ToDooo.Views;

/// <summary>
/// 独立轻量便签弹出面板：不依附主窗口坐标，可在主窗口按钮下方或系统托盘图标上方自由弹出，
/// 失焦自动隐藏，内置颜色筛选、内联搜索、开关控制、右键删除与弹簧物理拖拽上色。
/// </summary>
public sealed partial class StickyFlyoutWindow : Window
{
    private const int LogicalWidth = 328;
    private const int LogicalHeight = 380;
    private const double OrbRadius = 9.0;
    private const int TrailCount = 5;

    private readonly StickyNoteStore _store;
    private readonly SettingsService _settings;
    private readonly Func<string, bool> _isNoteOpen;
    private readonly Func<string?, StickyNote> _onCreateNew;
    private readonly Action<StickyNote, bool> _onSetNoteVisible;
    private readonly Action<StickyNote> _onBringToFront;
    private readonly Action<StickyNote> _onDelete;
    private readonly Func<StickyNote, bool, StickyNote> _onRestoreDeleted;
    private readonly Action<StickyNote> _onColorChanged;
    private readonly Action<StickyNote> _onTogglePin;
    private readonly Action<StickyNote> _onConvertToTodo;

    private StickyNote? _lastDeletedSnapshot;
    private bool _lastDeletedWasOpen;
    private DispatcherTimer? _undoTimer;

    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private DesktopAcrylicController? _acrylic;
    private SystemBackdropConfiguration? _backdropConfig;

    private string? _selectedColor;
    private readonly Dictionary<string, FontIcon> _tagChecks = new(StringComparer.OrdinalIgnoreCase);

    // 弹簧物理颜色拖拽状态
    private bool _tagPointerDown;
    private bool _tagDragging;
    private bool _suppressNextTagClick;
    private uint _tagPointerId;
    private string _dragColorKey = "yellow";
    private Color _dragColor;
    private Point _dragStart;
    private Point _dragOrigin;
    private Point _dragCursor;
    private Point _orbPos;
    private Point _orbVel;
    private readonly Point[] _trailPos = new Point[TrailCount];
    private readonly Ellipse?[] _trailDots = new Ellipse?[TrailCount];
    private Border? _orbVisual;
    private Grid? _orbPlusIcon;
    private ScaleTransform? _orbScale;
    private Ellipse? _orbGlow;
    private Ellipse? _burstRing;
    private Border? _hoveredCard;
    private StickyNote? _hoveredNote;
    private Brush? _hoveredOrigBrush;
    private Thickness _hoveredOrigThickness;
    private bool _hoveredNewButton;
    private Button? _dragSourceButton;
    private int _releaseMode; // 0=none, 1=snap-to-card, 2=return, 3=snap-to-new
    private double _releaseProgress;
    private Point _releaseFrom;
    private Point _releaseTo;
    private StickyNote? _pendingNote;
    private string? _pendingColorKey;
    private bool _renderHooked;
    private long _lastTick;

    public StickyFlyoutWindow(
        StickyNoteStore store,
        SettingsService settings,
        Func<string, bool> isNoteOpen,
        Func<string?, StickyNote> onCreateNew,
        Action<StickyNote, bool> onSetNoteVisible,
        Action<StickyNote> onBringToFront,
        Action<StickyNote> onDelete,
        Func<StickyNote, bool, StickyNote> onRestoreDeleted,
        Action<StickyNote> onColorChanged,
        Action<StickyNote> onTogglePin,
        Action<StickyNote> onConvertToTodo)
    {
        _store = store;
        _settings = settings;
        _isNoteOpen = isNoteOpen;
        _onCreateNew = onCreateNew;
        _onSetNoteVisible = onSetNoteVisible;
        _onBringToFront = onBringToFront;
        _onDelete = onDelete;
        _onRestoreDeleted = onRestoreDeleted;
        _onColorChanged = onColorChanged;
        _onTogglePin = onTogglePin;
        _onConvertToTodo = onConvertToTodo;

        InitializeComponent();
        Root.RequestedTheme = Appearance.ToElementTheme((ThemeMode)settings.Current.ThemeMode);
        ThemePalette.ApplyTextControlPalette(Root.Resources);

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));

        ConfigureWindow();
        ApplyBackdrop();

        Root.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(Root_DismissEmptySearch),
            handledEventsToo: true);

        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                CleanupDragOverlay();
                _appWindow.Hide();
            }
        };

        Closed += (_, _) =>
        {
            CleanupDragOverlay();
            WindowAcrylic.DetachAndDispose(ref _acrylic, ref _backdropConfig);
        };
    }

    public bool IsPopupVisible => _appWindow.IsVisible;

    public void RefreshTheme()
    {
        Root.RequestedTheme = Appearance.ToElementTheme((ThemeMode)_settings.Current.ThemeMode);
        ApplyBackdrop();
    }

    public void HidePopup()
    {
        CleanupDragOverlay();
        _appWindow.Hide();
    }

    public void ShowBelowScreenPoint(int anchorRightX, int anchorBottomY)
    {
        PrepareContent();
        var work = DisplayArea.GetFromPoint(new PointInt32(anchorRightX, anchorBottomY), DisplayAreaFallback.Nearest).WorkArea;
        // 先定位到目标显示器，再按目标显示器 DPI 重算物理尺寸，避免跨屏缩放偏差。
        _appWindow.Move(new PointInt32(work.X, work.Y));
        var (w, h) = GetPhysicalSize();
        var x = Math.Clamp(anchorRightX - w, work.X + 6, work.X + Math.Max(6, work.Width - w - 6));
        var y = Math.Clamp(anchorBottomY + 6, work.Y + 6, work.Y + Math.Max(6, work.Height - h - 6));
        ShowAt(x, y, w, h);
    }

    public void ShowAboveScreenPoint(int screenX, int screenY)
    {
        PrepareContent();
        var work = DisplayArea.GetFromPoint(new PointInt32(screenX, screenY), DisplayAreaFallback.Nearest).WorkArea;
        _appWindow.Move(new PointInt32(work.X, work.Y));
        var (w, h) = GetPhysicalSize();
        var x = Math.Clamp(screenX - w / 2, work.X + 8, work.X + Math.Max(8, work.Width - w - 8));
        var y = Math.Clamp(work.Y + work.Height - h - 8, work.Y + 8, work.Y + Math.Max(8, work.Height - h - 8));
        ShowAt(x, y, w, h);
    }

    private void PrepareContent()
    {
        CloseSearchBar();
        BuildTagChips();
        RebuildList();
    }

    private (int W, int H) GetPhysicalSize()
    {
        var scale = Math.Max(1.0, NativeMethods.GetDpiForWindow(_hwnd) / 96.0);
        return ((int)Math.Round(LogicalWidth * scale), (int)Math.Round(LogicalHeight * scale));
    }

    private void ShowAt(int x, int y, int w, int h)
    {
        _appWindow.MoveAndResize(new RectInt32(x, y, w, h));
        _appWindow.Show(activateWindow: true);
        NativeMethods.SetForegroundWindow(_hwnd);
    }

    private void ConfigureWindow()
    {
        _appWindow.IsShownInSwitchers = false;
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        var preference = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    private void ApplyBackdrop()
    {
        var dark = Appearance.IsDark((ThemeMode)_settings.Current.ThemeMode);
        var tintMode = (Appearance.TintMode)Math.Clamp(_settings.Current.WindowTintMode, 0, Appearance.TintCount - 1);
        var tint = Appearance.TintForOpacity(_settings.Current.WindowOpacity);
        if (_acrylic is null)
        {
            _backdropConfig = new SystemBackdropConfiguration
            {
                IsInputActive = true,
                Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light,
            };
            _acrylic = WindowAcrylic.Apply(this, tint, dark, _backdropConfig, tintMode, _settings.Current.CustomTintColor);
            return;
        }

        WindowAcrylic.ApplyTint(_acrylic, tint, dark, tintMode, _settings.Current.CustomTintColor);
        if (_backdropConfig is not null)
        {
            _backdropConfig.Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
        }
    }

    private void BuildTagChips()
    {
        TagPanel.Children.Clear();
        _tagChecks.Clear();
        var style = (Style)Application.Current.Resources["TagDotButtonStyle"];

        foreach (var theme in StickyColorTheme.All)
        {
            var dot = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(theme.Swatch),
            };
            var check = new FontIcon
            {
                Glyph = "\uE73E",
                FontSize = 9,
                Foreground = new SolidColorBrush(Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = string.Equals(_selectedColor, theme.Key, StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible
                    : Visibility.Collapsed,
            };
            var content = new Grid { Width = 18, Height = 18 };
            content.Children.Add(dot);
            content.Children.Add(check);

            var btn = new Button
            {
                Style = style,
                Content = content,
            };
            ToolTipService.SetToolTip(btn, $"{theme.Name}（点击筛选 / 拖动上色）");

            var colorKey = theme.Key;
            var swatch = theme.Swatch;
            btn.Click += (_, _) =>
            {
                if (_suppressNextTagClick)
                {
                    _suppressNextTagClick = false;
                    return;
                }

                _selectedColor = string.Equals(_selectedColor, colorKey, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : colorKey;
                foreach (var (k, c) in _tagChecks)
                {
                    c.Visibility = string.Equals(_selectedColor, k, StringComparison.OrdinalIgnoreCase)
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }

                RebuildList();
            };

            btn.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, pe) =>
                TagDot_PointerPressed(btn, colorKey, swatch, pe)), handledEventsToo: true);
            btn.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, pe) =>
                TagDot_PointerMoved(pe)), handledEventsToo: true);
            btn.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, pe) =>
                TagDot_PointerReleased(btn, pe)), handledEventsToo: true);
            btn.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, _) =>
                CancelTagDrag()), handledEventsToo: true);
            btn.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) =>
            {
                if (_tagDragging)
                {
                    FinishTagDrag();
                }
                else
                {
                    _tagPointerDown = false;
                }
            }), handledEventsToo: true);

            _tagChecks[colorKey] = check;
            TagPanel.Children.Add(btn);
        }
    }

    public void RebuildList()
    {
        ListPanel.Children.Clear();
        var keyword = SearchBox.Text.Trim();
        var all = _store.Notes;

        IEnumerable<StickyNote> query = all;
        if (!string.IsNullOrEmpty(_selectedColor))
        {
            query = query.Where(n => string.Equals(n.Color, _selectedColor, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(keyword))
        {
            query = query.Where(n => n.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = query
            .OrderByDescending(n => n.Pinned)
            .ThenByDescending(n => n.UpdatedAt)
            .ToList();
        EmptyText.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = all.Count == 0 ? "还没有便签，点右上角 + 新建一张" : "没有匹配的便签";

        foreach (var note in filtered)
        {
            ListPanel.Children.Add(BuildCard(note));
        }
    }

    private UIElement BuildCard(StickyNote note)
    {
        var palette = StickyColorTheme.Resolve(note.Color);
        var cardBg = (Brush)Application.Current.Resources["ItemCardBrush"];
        var cardHoverBg = (Brush)Application.Current.Resources["ItemCardHoverBrush"];

        var card = new Border
        {
            Style = (Style)Application.Current.Resources["TodoCardBorderStyle"],
            MinHeight = 46,
            Padding = new Thickness(10, 8, 6, 8),
            Margin = new Thickness(0),
            Tag = note,
        };

        var rootGrid = new Grid { ColumnSpacing = 8 };
        rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var colorBar = new Border
        {
            Tag = "StickyColorBar",
            Width = 3,
            CornerRadius = new CornerRadius(1.5),
            Background = new SolidColorBrush(palette.Swatch),
            Margin = new Thickness(0, 2, 0, 2),
        };
        Grid.SetColumn(colorBar, 0);
        rootGrid.Children.Add(colorBar);

        var isOpen = _isNoteOpen(note.Id);
        var textStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock
        {
            Text = note.Title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        });

        var preview = note.Preview;
        if (!string.IsNullOrEmpty(preview))
        {
            textStack.Children.Add(new TextBlock
            {
                Text = preview,
                FontSize = 11.5,
                Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
            });
        }

        var pin = note.Pinned ? " · 置顶" : string.Empty;
        textStack.Children.Add(new TextBlock
        {
            Text = $"{note.UpdatedAt.ToLocalTime():MM/dd HH:mm}{pin}",
            FontSize = 10.5,
            Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
        });

        Grid.SetColumn(textStack, 1);
        rootGrid.Children.Add(textStack);

        var swatchBrush = new SolidColorBrush(palette.Swatch);
        var (toggleHost, setToggleState) = BuildPillSwitch(
            isOpen,
            swatchBrush,
            onToggled: isOn => _onSetNoteVisible(note, isOn));
        Grid.SetColumn(toggleHost, 2);
        rootGrid.Children.Add(toggleHost);

        var contextMenu = new MenuFlyout();
        var pinItem = new MenuFlyoutItem
        {
            Text = note.Pinned ? "取消置顶" : "置顶便签",
            Icon = new FontIcon { Glyph = note.Pinned ? "\uE77A" : "\uE840" },
        };
        pinItem.Click += (_, _) =>
        {
            _onTogglePin(note);
            RebuildList();
        };
        contextMenu.Items.Add(pinItem);

        var toTodoItem = new MenuFlyoutItem
        {
            Text = "转为待办",
            Icon = new FontIcon { Glyph = "\uE73E" },
        };
        toTodoItem.Click += (_, _) => _onConvertToTodo(note);
        contextMenu.Items.Add(toTodoItem);

        contextMenu.Items.Add(new MenuFlyoutSeparator());

        var deleteItem = new MenuFlyoutItem
        {
            Text = "删除便签",
            Icon = new FontIcon { Glyph = "\uE74D" },
        };
        deleteItem.Click += (_, _) =>
        {
            var wasOpen = _isNoteOpen(note.Id);
            var snapshot = new StickyNote
            {
                Color = note.Color,
                Content = note.Content,
                Pinned = note.Pinned,
                FontSize = note.FontSize,
                X = note.X,
                Y = note.Y,
                Width = note.Width,
                Height = note.Height,
            };
            _onDelete(note);
            ListPanel.Children.Remove(card);
            UpdateEmptyState();
            ShowUndoBanner(snapshot, wasOpen);
        };
        contextMenu.Items.Add(deleteItem);
        card.ContextFlyout = contextMenu;

        card.Child = rootGrid;
        card.PointerEntered += (_, _) => card.Background = cardHoverBg;
        card.PointerExited += (_, _) => card.Background = cardBg;
        card.PointerPressed += (_, e) =>
        {
            if (Helpers.WindowDragHelper.IsPrimaryPointerPressed(card, e))
            {
                _onBringToFront(note);
                setToggleState(true);
            }
        };

        return card;
    }

    private static (Border Host, Action<bool> SetState) BuildPillSwitch(
        bool initialOn,
        SolidColorBrush onBrush,
        Action<bool> onToggled)
    {
        var isOn = initialOn;
        var offBg = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128));
        var offBorder = new SolidColorBrush(Color.FromArgb(95, 140, 140, 140));
        var thumbTransform = new TranslateTransform { X = initialOn ? 14 : 0 };

        var thumb = new Border
        {
            Width = 12,
            Height = 12,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Colors.White),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3, 0, 0, 0),
            RenderTransform = thumbTransform,
        };

        var track = new Border
        {
            Tag = onBrush,
            Width = 34,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = initialOn ? onBrush : offBg,
            BorderBrush = initialOn ? onBrush : offBorder,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(4, 0, 2, 0),
            Child = thumb,
        };
        ToolTipService.SetToolTip(track, initialOn ? "隐藏便签" : "显示便签");

        void ApplyVisual(bool state, bool animate)
        {
            isOn = state;
            track.Background = state ? onBrush : offBg;
            track.BorderBrush = state ? onBrush : offBorder;
            ToolTipService.SetToolTip(track, state ? "隐藏便签" : "显示便签");
            var targetX = state ? 14.0 : 0.0;
            if (!animate)
            {
                thumbTransform.X = targetX;
                return;
            }

            var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                To = targetX,
                Duration = new Duration(TimeSpan.FromMilliseconds(120)),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
                {
                    EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut,
                },
            };
            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, thumbTransform);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "X");
            sb.Children.Add(anim);
            sb.Begin();
        }

        track.PointerPressed += (_, pe) =>
        {
            if (Helpers.WindowDragHelper.IsPrimaryPointerPressed(track, pe))
            {
                pe.Handled = true;
                ApplyVisual(!isOn, animate: true);
                onToggled(isOn);
            }
        };

        return (track, state =>
        {
            if (isOn != state)
            {
                ApplyVisual(state, animate: true);
            }
        });
    }

    private void ShowUndoBanner(StickyNote snapshot, bool wasOpen)
    {
        _lastDeletedSnapshot = snapshot;
        _lastDeletedWasOpen = wasOpen;
        UndoText.Text = $"已删除「{snapshot.Title}」";
        UndoBar.Visibility = Visibility.Visible;

        if (_undoTimer is null)
        {
            _undoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _undoTimer.Tick += (_, _) => HideUndoBanner();
        }

        _undoTimer.Stop();
        _undoTimer.Start();
    }

    private void HideUndoBanner()
    {
        _undoTimer?.Stop();
        _lastDeletedSnapshot = null;
        UndoBar.Visibility = Visibility.Collapsed;
    }

    private void UndoDelete_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = _lastDeletedSnapshot;
        var wasOpen = _lastDeletedWasOpen;
        HideUndoBanner();
        if (snapshot is null)
        {
            return;
        }

        var restored = _onRestoreDeleted(snapshot, wasOpen);
        InsertNewCardSmoothly(restored);
    }

    private void UpdateEmptyState()
    {
        EmptyText.Visibility = ListPanel.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _store.Notes.Count == 0 ? "还没有便签，点右上角 + 新建一张" : "没有匹配的便签";
    }

    private void InsertNewCardSmoothly(StickyNote note)
    {
        var keyword = SearchBox.Text.Trim();
        var matchesColor = string.IsNullOrEmpty(_selectedColor) ||
                           string.Equals(note.Color, _selectedColor, StringComparison.OrdinalIgnoreCase);
        var matchesKeyword = string.IsNullOrEmpty(keyword) ||
                             note.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase);
        if (matchesColor && matchesKeyword)
        {
            ListPanel.Children.Insert(0, BuildCard(note));
            UpdateEmptyState();
        }
    }

    private void NewButton_Click(object sender, RoutedEventArgs e)
    {
        var created = _onCreateNew(_selectedColor);
        InsertNewCardSmoothly(created);
    }

    private void SearchAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (SearchBar.Visibility != Visibility.Visible)
        {
            TagPanel.Visibility = Visibility.Collapsed;
            SearchBar.Visibility = Visibility.Visible;
        }

        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        if (SearchBar.Visibility != Visibility.Visible)
        {
            TagPanel.Visibility = Visibility.Collapsed;
            SearchBar.Visibility = Visibility.Visible;
            SearchBox.Focus(FocusState.Programmatic);
        }
        else
        {
            CloseSearchBar();
        }
    }

    private void CloseSearchBar()
    {
        SearchBox.Text = string.Empty;
        ClearSearchButton.Visibility = Visibility.Collapsed;
        SearchBar.Visibility = Visibility.Collapsed;
        TagPanel.Visibility = Visibility.Visible;
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e) => CloseSearchBar();

    private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
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

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ClearSearchButton.Visibility = SearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RebuildList();
    }

    private void Root_DismissEmptySearch(object sender, PointerRoutedEventArgs e)
    {
        if (SearchBar.Visibility != Visibility.Visible || SearchBox.Text.Length > 0)
        {
            return;
        }

        var pos = e.GetCurrentPoint(ContentRoot).Position;
        if (IsInside(SearchBar, pos) || IsInside(SearchButton, pos))
        {
            return;
        }

        CloseSearchBar();
    }

    private bool IsInside(FrameworkElement element, Point rootPos)
    {
        if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return false;
        }

        var topLeft = element.TransformToVisual(ContentRoot).TransformPoint(new Point(0, 0));
        return rootPos.X >= topLeft.X && rootPos.X <= topLeft.X + element.ActualWidth &&
               rootPos.Y >= topLeft.Y && rootPos.Y <= topLeft.Y + element.ActualHeight;
    }

    // ---- 弹簧物理颜色拖拽 ------------------------------------------------

    private void TagDot_PointerPressed(Button source, string colorKey, Color swatch, PointerRoutedEventArgs e)
    {
        if (!Helpers.WindowDragHelper.IsPrimaryPointerPressed(source, e))
        {
            return;
        }

        if (_releaseMode != 0)
        {
            CleanupDragOverlay();
        }

        var pos = e.GetCurrentPoint(ContentRoot).Position;
        _tagPointerDown = true;
        _tagDragging = false;
        _suppressNextTagClick = false;
        _tagPointerId = e.Pointer.PointerId;
        _dragSourceButton = source;
        _dragColorKey = colorKey;
        _dragColor = swatch;
        _dragStart = pos;
        _dragCursor = pos;

        var topLeft = source.TransformToVisual(ContentRoot).TransformPoint(new Point(0, 0));
        _dragOrigin = new Point(topLeft.X + source.ActualWidth * 0.5, topLeft.Y + source.ActualHeight * 0.5);
    }

    private void TagDot_PointerMoved(PointerRoutedEventArgs e)
    {
        if (!_tagPointerDown || e.Pointer.PointerId != _tagPointerId)
        {
            return;
        }

        var pos = e.GetCurrentPoint(ContentRoot).Position;
        _dragCursor = pos;

        if (!_tagDragging)
        {
            var dx = pos.X - _dragStart.X;
            var dy = pos.Y - _dragStart.Y;
            if (dx * dx + dy * dy < 36)
            {
                return;
            }

            StartTagDrag(e);
        }

        UpdateHoveredCard(pos);
        e.Handled = true;
    }

    private void StartTagDrag(PointerRoutedEventArgs e)
    {
        _tagDragging = true;
        _suppressNextTagClick = true;
        _dragSourceButton?.CapturePointer(e.Pointer);

        _orbPos = _dragOrigin;
        _orbVel = new Point(0, 0);
        for (var i = 0; i < TrailCount; i++)
        {
            _trailPos[i] = _dragOrigin;
        }

        DragCanvas.Children.Clear();
        var solidBrush = new SolidColorBrush(_dragColor);
        var glowBrush = new SolidColorBrush(Color.FromArgb(64, _dragColor.R, _dragColor.G, _dragColor.B));

        for (var i = TrailCount - 1; i >= 0; i--)
        {
            var ratio = 1.0 - (i + 1.0) / (TrailCount + 1.5);
            var size = OrbRadius * 1.45 * ratio;
            var dot = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = solidBrush,
                Opacity = 0.42 * ratio,
                IsHitTestVisible = false,
            };
            _trailDots[i] = dot;
            DragCanvas.Children.Add(dot);
        }

        _orbGlow = new Ellipse
        {
            Width = OrbRadius * 3.1,
            Height = OrbRadius * 3.1,
            Fill = glowBrush,
            IsHitTestVisible = false,
        };
        DragCanvas.Children.Add(_orbGlow);

        var plusColor = Color.FromArgb(
            255,
            (byte)(_dragColor.R + (255 - _dragColor.R) * 0.50),
            (byte)(_dragColor.G + (255 - _dragColor.G) * 0.50),
            (byte)(_dragColor.B + (255 - _dragColor.B) * 0.50));
        var plusBrush = new SolidColorBrush(plusColor);

        _orbScale = new ScaleTransform { ScaleX = 0.85, ScaleY = 0.85 };
        _orbPlusIcon = new Grid
        {
            Width = 12,
            Height = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        _orbPlusIcon.Children.Add(new Border
        {
            Width = 11,
            Height = 2.6,
            CornerRadius = new CornerRadius(1.3),
            Background = plusBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        _orbPlusIcon.Children.Add(new Border
        {
            Width = 2.6,
            Height = 11,
            CornerRadius = new CornerRadius(1.3),
            Background = plusBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });

        _orbVisual = new Border
        {
            Width = OrbRadius * 2,
            Height = OrbRadius * 2,
            CornerRadius = new CornerRadius(OrbRadius),
            Background = solidBrush,
            Child = _orbPlusIcon,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _orbScale,
            IsHitTestVisible = false,
        };
        DragCanvas.Children.Add(_orbVisual);

        _burstRing = new Ellipse
        {
            Width = 22,
            Height = 22,
            Stroke = solidBrush,
            StrokeThickness = 2.2,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        DragCanvas.Children.Add(_burstRing);

        if (!_renderHooked)
        {
            _lastTick = System.Diagnostics.Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += Drag_Rendering;
            _renderHooked = true;
        }
    }

    private void TagDot_PointerReleased(Button source, PointerRoutedEventArgs e)
    {
        if (!_tagPointerDown || e.Pointer.PointerId != _tagPointerId)
        {
            return;
        }

        _tagPointerDown = false;
        if (!_tagDragging)
        {
            return;
        }

        FinishTagDrag();
        source.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void FinishTagDrag()
    {
        if (!_tagDragging)
        {
            return;
        }

        _tagPointerDown = false;
        _tagDragging = false;

        if (_hoveredNewButton)
        {
            var topLeft = NewButton.TransformToVisual(ContentRoot).TransformPoint(new Point(0, 0));
            _releaseMode = 3;
            _releaseProgress = 0;
            _releaseFrom = _orbPos;
            _releaseTo = new Point(topLeft.X + NewButton.ActualWidth * 0.5, topLeft.Y + NewButton.ActualHeight * 0.5);
            _pendingNote = null;
            _pendingColorKey = _dragColorKey;
        }
        else if (_hoveredCard is not null && _hoveredNote is not null)
        {
            var topLeft = _hoveredCard.TransformToVisual(ContentRoot).TransformPoint(new Point(0, 0));
            _releaseMode = 1;
            _releaseProgress = 0;
            _releaseFrom = _orbPos;
            _releaseTo = new Point(topLeft.X + 11.5, topLeft.Y + _hoveredCard.ActualHeight * 0.5);
            _pendingNote = _hoveredNote;
            _pendingColorKey = _dragColorKey;
        }
        else
        {
            _releaseMode = 2;
            _releaseProgress = 0;
            _releaseFrom = _orbPos;
            _releaseTo = _dragOrigin;
            _pendingNote = null;
            _pendingColorKey = null;
        }

        ClearHoveredCard();
    }

    private void UpdateHoveredCard(Point rootPos)
    {
        if (IsInside(NewButton, rootPos))
        {
            if (!_hoveredNewButton)
            {
                ClearHoveredCard();
                _hoveredNewButton = true;
                NewButton.Foreground = new SolidColorBrush(_dragColor);
                NewButton.Background = new SolidColorBrush(Color.FromArgb(40, _dragColor.R, _dragColor.G, _dragColor.B));
            }

            return;
        }

        if (_hoveredNewButton)
        {
            _hoveredNewButton = false;
            NewButton.ClearValue(Control.ForegroundProperty);
            NewButton.ClearValue(Control.BackgroundProperty);
        }

        Border? foundCard = null;
        StickyNote? foundNote = null;

        foreach (var child in ListPanel.Children)
        {
            if (child is not Border card || card.Tag is not StickyNote note)
            {
                continue;
            }

            var topLeft = card.TransformToVisual(ContentRoot).TransformPoint(new Point(0, 0));
            var bounds = new Rect(topLeft.X, topLeft.Y, card.ActualWidth, card.ActualHeight);
            if (bounds.Contains(rootPos))
            {
                foundCard = card;
                foundNote = note;
                break;
            }
        }

        if (ReferenceEquals(foundCard, _hoveredCard))
        {
            return;
        }

        ClearHoveredCard();
        if (foundCard is not null && foundNote is not null)
        {
            _hoveredCard = foundCard;
            _hoveredNote = foundNote;
            _hoveredOrigBrush = foundCard.BorderBrush;
            _hoveredOrigThickness = foundCard.BorderThickness;
            foundCard.BorderBrush = new SolidColorBrush(_dragColor);
            foundCard.BorderThickness = new Thickness(1.5);
        }
    }

    private void ClearHoveredCard()
    {
        if (_hoveredNewButton)
        {
            _hoveredNewButton = false;
            NewButton.ClearValue(Control.ForegroundProperty);
            NewButton.ClearValue(Control.BackgroundProperty);
        }

        if (_hoveredCard is not null)
        {
            _hoveredCard.BorderBrush = _hoveredOrigBrush;
            _hoveredCard.BorderThickness = _hoveredOrigThickness;
            _hoveredCard = null;
            _hoveredNote = null;
        }
    }

    private void CancelTagDrag()
    {
        if (_releaseMode != 0)
        {
            return;
        }

        if (_tagDragging)
        {
            _tagPointerDown = false;
            _tagDragging = false;
            CleanupDragOverlay();
        }
    }

    private void Drag_Rendering(object? sender, object e)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var dt = Math.Clamp((now - _lastTick) / (double)System.Diagnostics.Stopwatch.Frequency, 0.001, 0.033);
        _lastTick = now;

        if (_tagDragging)
        {
            var target = _dragCursor;
            var targetScale = 1.08;
            if (_hoveredNewButton)
            {
                var topLeft = NewButton.TransformToVisual(ContentRoot).TransformPoint(new Point(0, 0));
                var btnCenter = new Point(topLeft.X + NewButton.ActualWidth * 0.5, topLeft.Y + NewButton.ActualHeight * 0.5);
                target = new Point(
                    target.X + (btnCenter.X - target.X) * 0.72,
                    target.Y + (btnCenter.Y - target.Y) * 0.72);
                targetScale = 1.24;
            }

            const double stiffness = 360.0;
            const double damping = 26.0;
            var ax = (target.X - _orbPos.X) * stiffness - _orbVel.X * damping;
            var ay = (target.Y - _orbPos.Y) * stiffness - _orbVel.Y * damping;
            _orbVel = new Point(_orbVel.X + ax * dt, _orbVel.Y + ay * dt);
            _orbPos = new Point(_orbPos.X + _orbVel.X * dt, _orbPos.Y + _orbVel.Y * dt);

            if (_orbPlusIcon is not null)
            {
                var plusTarget = _hoveredNewButton ? 1.0 : 0.0;
                _orbPlusIcon.Opacity += (plusTarget - _orbPlusIcon.Opacity) * Math.Min(1.0, dt * 18.0);
            }

            UpdateTrail(dt, fade: _hoveredNewButton ? 0.35 : 1.0);
            PositionOrb(_orbPos, OrbRadius * 2, OrbRadius * 2, OrbRadius, targetScale);
            return;
        }

        if (_releaseMode == 1)
        {
            _releaseProgress = Math.Min(1.0, _releaseProgress + dt / 0.19);
            var t = 1.0 - Math.Pow(1.0 - _releaseProgress, 3);
            var pos = new Point(
                _releaseFrom.X + (_releaseTo.X - _releaseFrom.X) * t,
                _releaseFrom.Y + (_releaseTo.Y - _releaseFrom.Y) * t);
            var w = OrbRadius * 2 + (3.0 - OrbRadius * 2) * t;
            var h = OrbRadius * 2 + (28.0 - OrbRadius * 2) * t;
            var r = OrbRadius + (1.5 - OrbRadius) * t;

            UpdateTrail(dt, fade: 1.0 - t);
            PositionOrb(pos, w, h, r, 1.0);

            if (_releaseProgress >= 1.0)
            {
                var note = _pendingNote;
                var key = _pendingColorKey;
                CleanupDragOverlay();
                if (note is not null && key is not null && !string.Equals(note.Color, key, StringComparison.OrdinalIgnoreCase))
                {
                    note.Color = key;
                    _store.SaveMeta();
                    _onColorChanged(note);
                    if (!string.IsNullOrEmpty(_selectedColor))
                    {
                        RebuildList();
                    }
                    else
                    {
                        UpdateCardColorInPlace(note);
                    }
                }
            }

            return;
        }

        if (_releaseMode == 3)
        {
            _releaseProgress = Math.Min(1.0, _releaseProgress + dt / 0.22);
            var t = 1.0 - Math.Pow(1.0 - _releaseProgress, 3);
            var pos = new Point(
                _releaseFrom.X + (_releaseTo.X - _releaseFrom.X) * t,
                _releaseFrom.Y + (_releaseTo.Y - _releaseFrom.Y) * t);

            // 先弹大再轻盈收缩淡出，同时扩散一圈主题色波纹
            var scale = _releaseProgress < 0.35
                ? 1.24 + (_releaseProgress / 0.35) * 0.28
                : 1.52 - ((_releaseProgress - 0.35) / 0.65) * 0.92;
            var alpha = _releaseProgress < 0.35 ? 1.0 : 1.0 - (_releaseProgress - 0.35) / 0.65;

            if (_orbPlusIcon is not null)
            {
                _orbPlusIcon.Opacity = 1.0;
            }

            if (_orbVisual is not null)
            {
                _orbVisual.Opacity = Math.Max(0, alpha);
            }

            if (_orbGlow is not null)
            {
                _orbGlow.Opacity = Math.Max(0, alpha);
            }

            if (_burstRing is not null)
            {
                var ringSize = 20.0 + 26.0 * t;
                _burstRing.Width = ringSize;
                _burstRing.Height = ringSize;
                _burstRing.Opacity = Math.Max(0, 0.8 * (1.0 - t));
                Canvas.SetLeft(_burstRing, _releaseTo.X - ringSize * 0.5);
                Canvas.SetTop(_burstRing, _releaseTo.Y - ringSize * 0.5);
            }

            UpdateTrail(dt, fade: 1.0 - t);
            PositionOrb(pos, OrbRadius * 2, OrbRadius * 2, OrbRadius, scale);

            if (_releaseProgress >= 1.0)
            {
                var key = _pendingColorKey;
                CleanupDragOverlay();
                if (key is not null)
                {
                    var created = _onCreateNew(key);
                    InsertNewCardSmoothly(created);
                }
            }

            return;
        }

        if (_releaseMode == 2)
        {
            _releaseProgress = Math.Min(1.0, _releaseProgress + dt / 0.20);
            var t = 1.0 - Math.Pow(1.0 - _releaseProgress, 3);
            var pos = new Point(
                _releaseFrom.X + (_releaseTo.X - _releaseFrom.X) * t,
                _releaseFrom.Y + (_releaseTo.Y - _releaseFrom.Y) * t);

            UpdateTrail(dt, fade: 1.0 - t);
            PositionOrb(pos, OrbRadius * 2, OrbRadius * 2, OrbRadius, 1.0 - 0.15 * t);

            if (_releaseProgress >= 1.0)
            {
                CleanupDragOverlay();
            }

            return;
        }

        if (_renderHooked)
        {
            CompositionTarget.Rendering -= Drag_Rendering;
            _renderHooked = false;
        }
    }

    private void UpdateCardColorInPlace(StickyNote note)
    {
        var palette = StickyColorTheme.Resolve(note.Color);
        var swatchBrush = new SolidColorBrush(palette.Swatch);

        foreach (var child in ListPanel.Children)
        {
            if (child is not Border card || !ReferenceEquals(card.Tag, note) || card.Child is not Grid grid)
            {
                continue;
            }

            foreach (var elem in grid.Children)
            {
                if (elem is Border { Tag: "StickyColorBar" } bar)
                {
                    bar.Background = swatchBrush;
                }
                else if (elem is Border { Tag: SolidColorBrush existingBrush })
                {
                    existingBrush.Color = palette.Swatch;
                }
            }

            break;
        }
    }

    private void UpdateTrail(double dt, double fade = 1.0)
    {
        var follow = 1.0 - Math.Exp(-dt * 28.0);
        var prev = _orbPos;
        for (var i = 0; i < TrailCount; i++)
        {
            _trailPos[i] = new Point(
                _trailPos[i].X + (prev.X - _trailPos[i].X) * follow,
                _trailPos[i].Y + (prev.Y - _trailPos[i].Y) * follow);
            prev = _trailPos[i];

            if (_trailDots[i] is { } dot)
            {
                Canvas.SetLeft(dot, _trailPos[i].X - dot.Width * 0.5);
                Canvas.SetTop(dot, _trailPos[i].Y - dot.Height * 0.5);
                var ratio = 1.0 - (i + 1.0) / (TrailCount + 1.5);
                dot.Opacity = 0.42 * ratio * fade;
            }
        }
    }

    private void PositionOrb(Point center, double w, double h, double radius, double scale)
    {
        if (_orbVisual is not null)
        {
            _orbVisual.Width = w;
            _orbVisual.Height = h;
            _orbVisual.CornerRadius = new CornerRadius(radius);
            Canvas.SetLeft(_orbVisual, center.X - w * 0.5);
            Canvas.SetTop(_orbVisual, center.Y - h * 0.5);
            if (_orbScale is not null)
            {
                _orbScale.ScaleX = scale;
                _orbScale.ScaleY = scale;
            }
        }

        if (_orbGlow is not null)
        {
            Canvas.SetLeft(_orbGlow, center.X - _orbGlow.Width * 0.5);
            Canvas.SetTop(_orbGlow, center.Y - _orbGlow.Height * 0.5);
        }
    }

    private void CleanupDragOverlay()
    {
        if (_renderHooked)
        {
            CompositionTarget.Rendering -= Drag_Rendering;
            _renderHooked = false;
        }

        ClearHoveredCard();
        _releaseMode = 0;
        _pendingNote = null;
        _pendingColorKey = null;
        DragCanvas.Children.Clear();
        _orbVisual = null;
        _orbPlusIcon = null;
        _orbScale = null;
        _orbGlow = null;
        _burstRing = null;
        Array.Clear(_trailDots);
    }
}
