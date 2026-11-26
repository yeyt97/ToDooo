using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using ToDooo.Models;
using Windows.Foundation;
using Windows.UI;

namespace ToDooo;

/// <summary>
/// 顶部颜色标签拖拽到待办条目：
/// - 拖起时轻盈弹起，移动时带柔和光晕与轻快彗尾微粒跟随；
/// - 悬停到待办卡片时，卡片边框亮起并在左侧标签槽显示预览竖条；
/// - 释放到条目上时，圆点飞入标签槽并弹性形变为 3×20 竖条，随后写入标签；
/// - 支持 Ctrl+Z 逐次撤销拖拽改色（输入框聚焦时不拦截文本撤销）。
/// </summary>
public sealed partial class MainWindow
{
    private const double TagDragThreshold = 5.0;
    private const double TagOrbRadius = 9.0;
    private const int TrailCount = 5;

    private readonly Stack<(string Description, Action Undo)> _quickUndoStack = new();

    private bool _tagPointerDown;
    private bool _isTagDragging;
    private bool _suppressNextTagClick;
    private uint _tagPointerId;
    private int _dragTagIndex = -1;
    private Color _dragTagColor;
    private Point _dragStartPoint;
    private Point _dragOriginCenter;
    private Point _dragCursor;
    private Button? _dragSourceButton;

    // 拖拽浮层与彗尾状态
    private double _orbX;
    private double _orbY;
    private double _dragElapsed;
    private readonly Point[] _trailPoints = new Point[TrailCount];
    private readonly Ellipse?[] _trailDots = new Ellipse?[TrailCount];

    private Ellipse? _orbGlow;
    private Border? _orbVisual;
    private ScaleTransform? _orbScale;
    private Ellipse? _rippleRing;

    // 悬停高亮的待办卡片
    private TodoItem? _hoveredTodoItem;
    private Border? _hoveredTodoCard;
    private Brush? _hoveredOriginalBorderBrush;
    private Thickness _hoveredOriginalBorderThickness;

    // 释放后的形变/回弹动画状态
    private enum TagReleaseMode { None, MorphToBar, SnapBack }
    private TagReleaseMode _releaseMode;
    private double _releaseElapsed;
    private double _releaseDuration;
    private Point _releaseStart;
    private Point _releaseTarget;
    private TodoItem? _releaseTargetItem;
    private int _releaseTagIndex;
    private bool _releaseTagApplied;

    private bool _renderLoopHooked;
    private readonly Stopwatch _tagAnimClock = new();
    private double _lastFrameSeconds;

    private void UndoAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // 输入框内保留系统自带的文本撤销。
        if (FocusManager.GetFocusedElement(Root.XamlRoot) is TextBox)
        {
            return;
        }

        if (_quickUndoStack.Count > 0)
        {
            if (_undoConfirmPending)
            {
                ExecuteTopQuickUndo();
            }
            else
            {
                var (description, _) = _quickUndoStack.Peek();
                ShowUndoConfirm(description);
            }

            args.Handled = true;
            return;
        }

        if (_undoItems is { Count: > 0 } || _undoPostpone is { Count: > 0 })
        {
            UndoButton_Click(this, new RoutedEventArgs());
            args.Handled = true;
        }
    }

    private void ExecuteTopQuickUndo()
    {
        _undoConfirmPending = false;
        if (_quickUndoStack.Count == 0)
        {
            HideUndo();
            return;
        }

        var (description, undo) = _quickUndoStack.Pop();
        undo();
        UpdateTagVisuals();
        ShowToast($"已撤销{description}");
    }

    private void AttachTagDragHandlers(Button button, int tagIndex, Color color)
    {
        button.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler((_, e) => TagDot_PointerPressed(button, tagIndex, color, e)),
            handledEventsToo: true);
        button.AddHandler(
            UIElement.PointerMovedEvent,
            new PointerEventHandler((_, e) => TagDot_PointerMoved(e)),
            handledEventsToo: true);
        button.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler((_, e) => TagDot_PointerReleased(e)),
            handledEventsToo: true);
        button.AddHandler(
            UIElement.PointerCanceledEvent,
            new PointerEventHandler((_, _) => CancelTagDrag()),
            handledEventsToo: true);
        button.AddHandler(
            UIElement.PointerCaptureLostEvent,
            new PointerEventHandler((_, _) =>
            {
                if (_isTagDragging)
                {
                    FinishTagDrag();
                }
                else
                {
                    _tagPointerDown = false;
                }
            }),
            handledEventsToo: true);
    }

    private bool ConsumeSuppressedTagClick()
    {
        if (!_suppressNextTagClick)
        {
            return false;
        }

        _suppressNextTagClick = false;
        return true;
    }

    private void TagDot_PointerPressed(Button button, int tagIndex, Color color, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (_releaseMode != TagReleaseMode.None)
        {
            CleanupTagOverlay();
        }

        _tagPointerDown = true;
        _isTagDragging = false;
        _suppressNextTagClick = false;
        _tagPointerId = e.Pointer.PointerId;
        _dragTagIndex = tagIndex;
        _dragTagColor = color;
        _dragSourceButton = button;
        _dragStartPoint = point.Position;
        _dragCursor = point.Position;

        var topLeft = button.TransformToVisual(Root).TransformPoint(new Point(0, 0));
        _dragOriginCenter = new Point(
            topLeft.X + button.ActualWidth * 0.5,
            topLeft.Y + button.ActualHeight * 0.5);
    }

    private void TagDot_PointerMoved(PointerRoutedEventArgs e)
    {
        if (!_tagPointerDown || e.Pointer.PointerId != _tagPointerId)
        {
            return;
        }

        var pos = e.GetCurrentPoint(Root).Position;
        _dragCursor = pos;

        if (!_isTagDragging)
        {
            var dx = pos.X - _dragStartPoint.X;
            var dy = pos.Y - _dragStartPoint.Y;
            if (dx * dx + dy * dy < TagDragThreshold * TagDragThreshold)
            {
                return;
            }

            StartTagDrag(e.Pointer);
        }

        UpdateHoveredTodo(pos);
        e.Handled = true;
    }

    private void TagDot_PointerReleased(PointerRoutedEventArgs e)
    {
        if (!_tagPointerDown || e.Pointer.PointerId != _tagPointerId)
        {
            return;
        }

        _tagPointerDown = false;
        if (!_isTagDragging)
        {
            return;
        }

        _dragCursor = e.GetCurrentPoint(Root).Position;
        UpdateHoveredTodo(_dragCursor);
        FinishTagDrag();
        e.Handled = true;
    }

    private void StartTagDrag(Pointer pointer)
    {
        _isTagDragging = true;
        _suppressNextTagClick = true;
        _dragElapsed = 0;
        _orbX = _dragCursor.X;
        _orbY = _dragCursor.Y;

        for (var i = 0; i < TrailCount; i++)
        {
            _trailPoints[i] = new Point(_orbX, _orbY);
        }

        _dragSourceButton?.CapturePointer(pointer);
        TagDragOverlay.Children.Clear();

        var solidBrush = new SolidColorBrush(_dragTagColor);
        var glowBrush = new SolidColorBrush(Color.FromArgb(64, _dragTagColor.R, _dragTagColor.G, _dragTagColor.B));

        // 1. 轻盈彗尾微粒（从尾到头逐级放大增亮）
        for (var i = TrailCount - 1; i >= 0; i--)
        {
            var ratio = 1.0 - (i + 1.0) / (TrailCount + 1.5);
            var size = TagOrbRadius * 1.45 * ratio;
            var dot = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = solidBrush,
                Opacity = 0.42 * ratio,
                IsHitTestVisible = false,
            };
            _trailDots[i] = dot;
            TagDragOverlay.Children.Add(dot);
        }

        // 3. 柔和呼吸光晕
        _orbGlow = new Ellipse
        {
            Width = TagOrbRadius * 3.1,
            Height = TagOrbRadius * 3.1,
            Fill = glowBrush,
            IsHitTestVisible = false,
        };
        TagDragOverlay.Children.Add(_orbGlow);

        // 4. 拖拽圆点本体（带轻盈弹起缩放，释放时形变为 3×20 竖条）
        _orbScale = new ScaleTransform { ScaleX = 0.85, ScaleY = 0.85 };
        _orbVisual = new Border
        {
            Width = TagOrbRadius * 2,
            Height = TagOrbRadius * 2,
            CornerRadius = new CornerRadius(TagOrbRadius),
            Background = solidBrush,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _orbScale,
            IsHitTestVisible = false,
        };
        TagDragOverlay.Children.Add(_orbVisual);

        StartRenderLoop();
    }

    private void FinishTagDrag()
    {
        _isTagDragging = false;
        _tagPointerDown = false;
        _dragSourceButton?.ReleasePointerCaptures();

        for (var i = 0; i < TrailCount; i++)
        {
            if (_trailDots[i] is { } dot)
            {
                dot.Visibility = Visibility.Collapsed;
            }
        }

        if (_orbScale is not null)
        {
            _orbScale.ScaleX = 1;
            _orbScale.ScaleY = 1;
        }

        var targetItem = _hoveredTodoItem;
        var targetCard = _hoveredTodoCard;
        ClearHoveredTodo();

        if (targetItem is not null && targetCard is not null)
        {
            _releaseMode = TagReleaseMode.MorphToBar;
            _releaseElapsed = 0;
            _releaseDuration = 0.30;
            _releaseStart = new Point(_orbX, _orbY);
            _releaseTarget = ComputeRowTagBarCenter(targetCard);
            _releaseTargetItem = targetItem;
            _releaseTagIndex = _dragTagIndex;
            _releaseTagApplied = false;

            _rippleRing = new Ellipse
            {
                Width = 14,
                Height = 14,
                Stroke = new SolidColorBrush(_dragTagColor),
                StrokeThickness = 2,
                Opacity = 0,
                IsHitTestVisible = false,
            };
            TagDragOverlay.Children.Insert(0, _rippleRing);
        }
        else
        {
            _releaseMode = TagReleaseMode.SnapBack;
            _releaseElapsed = 0;
            _releaseDuration = 0.20;
            _releaseStart = new Point(_orbX, _orbY);
            _releaseTarget = _dragOriginCenter;
            _releaseTargetItem = null;
        }
    }

    private void CancelTagDrag()
    {
        _tagPointerDown = false;
        _isTagDragging = false;
        _dragSourceButton?.ReleasePointerCaptures();
        ClearHoveredTodo();
        CleanupTagOverlay();
    }

    private void StartRenderLoop()
    {
        if (_renderLoopHooked)
        {
            return;
        }

        _renderLoopHooked = true;
        _tagAnimClock.Restart();
        _lastFrameSeconds = 0;
        CompositionTarget.Rendering += TagDrag_Rendering;
    }

    private void StopRenderLoop()
    {
        if (!_renderLoopHooked)
        {
            return;
        }

        _renderLoopHooked = false;
        CompositionTarget.Rendering -= TagDrag_Rendering;
        _tagAnimClock.Stop();
    }

    private void TagDrag_Rendering(object? sender, object e)
    {
        var now = _tagAnimClock.Elapsed.TotalSeconds;
        var dt = Math.Clamp(now - _lastFrameSeconds, 0.001, 0.05);
        _lastFrameSeconds = now;

        if (_isTagDragging)
        {
            StepDragAnimation(dt);
        }
        else if (_releaseMode == TagReleaseMode.MorphToBar)
        {
            StepMorphToBar(dt);
        }
        else if (_releaseMode == TagReleaseMode.SnapBack)
        {
            StepSnapBack(dt);
        }
        else
        {
            StopRenderLoop();
        }
    }

    private void StepDragAnimation(double dt)
    {
        _dragElapsed += dt;

        // 轻快顺滑跟随（无拖沓滞后）
        var follow = 1.0 - Math.Exp(-28.0 * dt);
        _orbX = Lerp(_orbX, _dragCursor.X, follow);
        _orbY = Lerp(_orbY, _dragCursor.Y, follow);

        // 拖起瞬间轻盈弹跳（0.85 -> 1.15 -> 1.0），悬停条目时微微放大
        if (_orbScale is not null)
        {
            var popT = Math.Clamp(_dragElapsed / 0.16, 0.0, 1.0);
            var baseScale = _hoveredTodoItem is not null ? 1.10 : 1.0;
            var pop = popT < 1.0
                ? Lerp(0.85, baseScale, popT) + Math.Sin(popT * Math.PI) * 0.14
                : baseScale;
            _orbScale.ScaleX = pop;
            _orbScale.ScaleY = pop;
        }

        // 柔和呼吸光晕
        if (_orbGlow is not null)
        {
            var pulse = 1.0 + Math.Sin(_dragElapsed * 7.0) * 0.10;
            var glowSize = TagOrbRadius * 3.1 * pulse;
            _orbGlow.Width = glowSize;
            _orbGlow.Height = glowSize;
        }

        PositionOrb(_orbX, _orbY, TagOrbRadius * 2, TagOrbRadius * 2, TagOrbRadius);

        // 彗尾微粒级联跟随
        var trailFollow = 1.0 - Math.Exp(-20.0 * dt);
        for (var i = 0; i < TrailCount; i++)
        {
            var prev = i == 0 ? new Point(_orbX, _orbY) : _trailPoints[i - 1];
            _trailPoints[i] = new Point(
                Lerp(_trailPoints[i].X, prev.X, trailFollow),
                Lerp(_trailPoints[i].Y, prev.Y, trailFollow));

            if (_trailDots[i] is { } dot)
            {
                Canvas.SetLeft(dot, _trailPoints[i].X - dot.Width * 0.5);
                Canvas.SetTop(dot, _trailPoints[i].Y - dot.Height * 0.5);
            }
        }
    }

    private void StepMorphToBar(double dt)
    {
        _releaseElapsed += dt;
        var t = Math.Clamp(_releaseElapsed / _releaseDuration, 0.0, 1.0);

        // 位置：快进稳落（EaseOutCubic）
        var posT = 1.0 - Math.Pow(1.0 - t, 3);
        var x = Lerp(_releaseStart.X, _releaseTarget.X, posT);
        var y = Lerp(_releaseStart.Y, _releaseTarget.Y, posT);

        // 尺寸：18×18 圆点 -> 3×20 竖条，高度带轻盈弹性回冲
        var morphT = EaseInOutCubic(Math.Clamp((t - 0.06) / 0.84, 0.0, 1.0));
        var width = Lerp(TagOrbRadius * 2, 3.0, morphT);
        var overshoot = Math.Sin(morphT * Math.PI) * 4.5;
        var height = Lerp(TagOrbRadius * 2, 20.0, morphT) + overshoot;
        var radius = Lerp(TagOrbRadius, 1.5, morphT);

        PositionOrb(x, y, width, height, radius);

        if (_orbGlow is not null)
        {
            _orbGlow.Opacity = Math.Max(0.0, 1.0 - t * 1.4);
        }

        if (_rippleRing is not null)
        {
            var rt = Math.Clamp((t - 0.22) / 0.78, 0.0, 1.0);
            var ringSize = Lerp(12.0, 34.0, 1.0 - Math.Pow(1.0 - rt, 2));
            _rippleRing.Width = ringSize;
            _rippleRing.Height = ringSize;
            _rippleRing.Opacity = rt > 0 ? (1.0 - rt) * 0.55 : 0;
            Canvas.SetLeft(_rippleRing, _releaseTarget.X - ringSize * 0.5);
            Canvas.SetTop(_rippleRing, _releaseTarget.Y - ringSize * 0.5);
        }

        // 接近落地时写入实际标签并压入 Ctrl+Z 撤销栈
        if (!_releaseTagApplied && t >= 0.78 && _releaseTargetItem is not null)
        {
            _releaseTagApplied = true;
            var targetItem = _releaseTargetItem;
            var previousTag = targetItem.TagIndex;
            if (previousTag != _releaseTagIndex)
            {
                _quickUndoStack.Push(("颜色标签", () => ViewModel.SetTag(targetItem, previousTag)));
                ViewModel.SetTag(targetItem, _releaseTagIndex);
            }
        }

        if (_orbVisual is not null && t > 0.82)
        {
            _orbVisual.Opacity = Math.Max(0.0, 1.0 - (t - 0.82) / 0.18);
        }

        if (t >= 1.0)
        {
            CleanupTagOverlay();
        }
    }

    private void StepSnapBack(double dt)
    {
        _releaseElapsed += dt;
        var t = Math.Clamp(_releaseElapsed / _releaseDuration, 0.0, 1.0);
        var ease = 1.0 - Math.Pow(1.0 - t, 3);

        var x = Lerp(_releaseStart.X, _releaseTarget.X, ease);
        var y = Lerp(_releaseStart.Y, _releaseTarget.Y, ease);
        var size = Lerp(TagOrbRadius * 2, TagOrbRadius * 1.4, ease);

        PositionOrb(x, y, size, size, size * 0.5);

        if (_orbVisual is not null)
        {
            _orbVisual.Opacity = 1.0 - ease * 0.85;
        }
        if (_orbGlow is not null)
        {
            _orbGlow.Opacity = 1.0 - ease;
        }

        if (t >= 1.0)
        {
            CleanupTagOverlay();
        }
    }

    private void PositionOrb(double cx, double cy, double width, double height, double cornerRadius)
    {
        if (_orbVisual is not null)
        {
            _orbVisual.Width = width;
            _orbVisual.Height = height;
            _orbVisual.CornerRadius = new CornerRadius(cornerRadius);
            Canvas.SetLeft(_orbVisual, cx - width * 0.5);
            Canvas.SetTop(_orbVisual, cy - height * 0.5);
        }

        if (_orbGlow is not null)
        {
            Canvas.SetLeft(_orbGlow, cx - _orbGlow.Width * 0.5);
            Canvas.SetTop(_orbGlow, cy - _orbGlow.Height * 0.5);
        }
    }

    private void UpdateHoveredTodo(Point rootPos)
    {
        TodoItem? foundItem = null;
        Border? foundCard = null;

        for (var i = 0; i < ViewModel.Items.Count; i++)
        {
            if (TodoList.ContainerFromIndex(i) is not ListViewItem container)
            {
                continue;
            }

            var topLeft = container.TransformToVisual(Root).TransformPoint(new Point(0, 0));
            var bounds = new Rect(topLeft.X, topLeft.Y, container.ActualWidth, container.ActualHeight);
            if (!bounds.Contains(rootPos))
            {
                continue;
            }

            var card = FindFirstChild<Border>(container);
            if (card is not null &&
                FindTaggedChild<FrameworkElement>(card, "SubtaskArea") is { Visibility: Visibility.Visible, ActualHeight: > 0 } subtaskArea)
            {
                var subTop = subtaskArea.TransformToVisual(Root).TransformPoint(new Point(0, 0)).Y;
                if (rootPos.Y >= subTop)
                {
                    break;
                }
            }

            foundItem = ViewModel.Items[i];
            foundCard = card;
            break;
        }

        if (ReferenceEquals(foundCard, _hoveredTodoCard))
        {
            return;
        }

        ClearHoveredTodo();

        if (foundItem is not null && foundCard is not null)
        {
            _hoveredTodoItem = foundItem;
            _hoveredTodoCard = foundCard;
            _hoveredOriginalBorderBrush = foundCard.BorderBrush;
            _hoveredOriginalBorderThickness = foundCard.BorderThickness;
            foundCard.BorderBrush = new SolidColorBrush(_dragTagColor);
            foundCard.BorderThickness = new Thickness(1.5);
        }
    }

    private void ClearHoveredTodo()
    {
        if (_hoveredTodoCard is not null)
        {
            _hoveredTodoCard.BorderBrush = _hoveredOriginalBorderBrush;
            _hoveredTodoCard.BorderThickness = _hoveredOriginalBorderThickness;
        }

        _hoveredTodoItem = null;
        _hoveredTodoCard = null;
        _hoveredOriginalBorderBrush = null;
    }

    private Point ComputeRowTagBarCenter(Border card)
    {
        var tagBar = FindDescendantByTag<Border>(card, "RowTagBar");
        if (tagBar is not null && tagBar.Visibility == Visibility.Visible && tagBar.ActualWidth > 0)
        {
            var pt = tagBar.TransformToVisual(Root).TransformPoint(new Point(0, 0));
            return new Point(pt.X + tagBar.ActualWidth * 0.5, pt.Y + tagBar.ActualHeight * 0.5);
        }

        var checkBox = FindFirstChild<CheckBox>(card);
        if (checkBox is not null)
        {
            var cbPt = checkBox.TransformToVisual(Root).TransformPoint(new Point(0, 0));
            return new Point(cbPt.X + checkBox.ActualWidth + 3.5, cbPt.Y + checkBox.ActualHeight * 0.5);
        }

        var cardPt = card.TransformToVisual(Root).TransformPoint(new Point(0, 0));
        return new Point(cardPt.X + 42.0, cardPt.Y + card.ActualHeight * 0.5);
    }

    private void CleanupTagOverlay()
    {
        _releaseMode = TagReleaseMode.None;
        _releaseTargetItem = null;
        _orbGlow = null;
        _orbVisual = null;
        _orbScale = null;
        _rippleRing = null;
        Array.Clear(_trailDots, 0, _trailDots.Length);
        TagDragOverlay.Children.Clear();
        StopRenderLoop();
    }

    private static T? FindFirstChild<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            var nested = FindFirstChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static T? FindDescendantByTag<T>(DependencyObject root, string tag) where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && Equals(match.Tag, tag))
            {
                return match;
            }

            var nested = FindDescendantByTag<T>(child, tag);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static double EaseInOutCubic(double t)
        => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
}
