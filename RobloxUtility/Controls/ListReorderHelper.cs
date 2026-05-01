using System.Collections;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using KeyEventHandler = System.Windows.Input.KeyEventHandler;
using ListBox = System.Windows.Controls.ListBox;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace RobloxUtility.Controls;

/// <summary>
/// Swapy-style list reorder: mouse-capture drag, floating ghost card, and springy FLIP transitions when rows swap.
/// </summary>
public static class ListReorder
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(ListReorder),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty IsActiveDragSourceProperty = DependencyProperty.RegisterAttached(
        "IsActiveDragSource",
        typeof(bool),
        typeof(ListReorder),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static void SetIsActiveDragSource(ListBoxItem element, bool value) => element.SetValue(IsActiveDragSourceProperty, value);

    public static bool GetIsActiveDragSource(ListBoxItem element) => (bool)element.GetValue(IsActiveDragSourceProperty);

    public static readonly RoutedEvent ReorderCompletedEvent = EventManager.RegisterRoutedEvent(
        "ReorderCompleted",
        RoutingStrategy.Bubble,
        typeof(RoutedEventHandler),
        typeof(ListReorder));

    public static void SetIsEnabled(ListBox element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(ListBox element) => (bool)element.GetValue(IsEnabledProperty);

    public static void AddReorderCompletedHandler(DependencyObject d, RoutedEventHandler handler)
    {
        if (d is UIElement u)
        {
            u.AddHandler(ReorderCompletedEvent, handler);
        }
    }

    public static void RemoveReorderCompletedHandler(DependencyObject d, RoutedEventHandler handler)
    {
        if (d is UIElement u)
        {
            u.RemoveHandler(ReorderCompletedEvent, handler);
        }
    }

    private static readonly ConditionalWeakTable<ListBox, ReorderSession> Sessions = new();

    private sealed class ReorderSession
    {
        public List<object?>? OrderSnapshot;
    }

    private sealed class DragPrep
    {
        public ListBox ListBox = null!;
        public ListBoxItem Item = null!;
        public object? Data;
        public Point StartScreen;
    }

    private sealed class ActiveDragSession
    {
        public ListBox ListBox = null!;
        public Window HostWindow = null!;
        public ListBoxItem SourceItem = null!;
        public object DragData = null!;
        public GhostDragPopup? Ghost;
        public int LastInsert = int.MinValue;
        public KeyEventHandler? EscapeHandler;
    }

    private static DragPrep? _prep;
    private static ListBoxItem? _pressedItem;
    private static ActiveDragSession? _active;

    private enum RowDragPhase
    {
        Normal,
        Pressed
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox lb)
        {
            return;
        }

        if (e.NewValue is true)
        {
            _ = Sessions.GetValue(lb, _ => new ReorderSession());
            lb.PreviewMouseLeftButtonDown += List_PreviewMouseLeftButtonDown;
            lb.PreviewMouseMove += List_PreviewMouseMove;
            lb.PreviewMouseLeftButtonUp += List_PreviewMouseLeftButtonUp;
            lb.LostMouseCapture += List_LostMouseCapture;
        }
        else
        {
            lb.PreviewMouseLeftButtonDown -= List_PreviewMouseLeftButtonDown;
            lb.PreviewMouseMove -= List_PreviewMouseMove;
            lb.PreviewMouseLeftButtonUp -= List_PreviewMouseLeftButtonUp;
            lb.LostMouseCapture -= List_LostMouseCapture;
        }
    }

    private static void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox lb || !GetIsEnabled(lb) || _active is not null)
        {
            return;
        }

        if (e.OriginalSource is not DependencyObject src)
        {
            return;
        }

        var lbi = FindParent<ListBoxItem>(src);
        if (lbi is null || lbi.DataContext is null)
        {
            return;
        }

        _prep = new DragPrep
        {
            ListBox = lb,
            Item = lbi,
            Data = lbi.DataContext,
            StartScreen = e.GetPosition(null)
        };

        _pressedItem = lbi;
        SetRowDragPhase(lbi, RowDragPhase.Pressed);
    }

    private static void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not ListBox lb || !GetIsEnabled(lb))
        {
            return;
        }

        if (_active is { } session && ReferenceEquals(session.ListBox, lb))
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndDragSession(commit: true);
                return;
            }

            DragSessionMouseMove(lb, session, e);
            e.Handled = true;
            return;
        }

        if (_prep is null || !ReferenceEquals(_prep.ListBox, lb) || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var now = e.GetPosition(null);
        var dx = now.X - _prep.StartScreen.X;
        var dy = now.Y - _prep.StartScreen.Y;
        if (Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (lb.ItemsSource is not IList list || _prep.Data is null)
        {
            _prep = null;
            return;
        }

        var lbi = _prep.Item;
        var data = _prep.Data;
        _prep = null;

        if (_pressedItem is not null && !ReferenceEquals(_pressedItem, lbi))
        {
            SetRowDragPhase(_pressedItem, RowDragPhase.Normal);
        }

        _pressedItem = null;
        SetRowDragPhase(lbi, RowDragPhase.Normal);

        StartDragSession(lb, lbi, data, list, e);
        e.Handled = true;
    }

    private static void StartDragSession(ListBox lb, ListBoxItem source, object data, IList list, MouseEventArgs e)
    {
        if (Window.GetWindow(lb) is not { } win)
        {
            return;
        }

        var s = Sessions.GetValue(lb, _ => new ReorderSession());
        s.OrderSnapshot = new List<object?>(list.Count);
        foreach (var x in list)
        {
            s.OrderSnapshot.Add(x);
        }

        source.UpdateLayout();
        var w = Math.Max(1, source.ActualWidth);
        var h = Math.Max(1, source.ActualHeight);
        var size = new Size(w, h);

        // Snapshot and popup before mutating the row (opacity / drag trigger).
        var ghost = new GhostDragPopup(win, source, e, size);

        SetIsActiveDragSource(source, true);
        source.Opacity = 0.14;
        _ = lb.CaptureMouse();

        var session = new ActiveDragSession
        {
            ListBox = lb,
            HostWindow = win,
            SourceItem = source,
            DragData = data,
            Ghost = ghost,
            LastInsert = int.MinValue
        };
        _active = session;

        void OnEscape(object _, KeyEventArgs ke)
        {
            if (ke.Key != Key.Escape || _active is null)
            {
                return;
            }

            ke.Handled = true;
            EndDragSession(commit: false);
        }

        session.EscapeHandler = OnEscape;
        win.PreviewKeyDown += OnEscape;

        DragSessionMouseMove(lb, session, e);
    }

    private static void DragSessionMouseMove(ListBox lb, ActiveDragSession session, MouseEventArgs e)
    {
        if (session.Ghost is null || lb.ItemsSource is not IList list)
        {
            return;
        }

        session.Ghost.Move(session.HostWindow, e);

        var mouseLb = e.GetPosition(lb);
        var insert = GetInsertBeforeIndex(lb, mouseLb);
        if (insert == session.LastInsert)
        {
            return;
        }

        session.LastInsert = insert;
        var tops = RecordItemTops(lb);
        if (!ApplyMoveByItem(list, session.DragData, insert))
        {
            return;
        }

        lb.UpdateLayout();
        PlayFlipAnimations(lb, tops);
    }

    private static void List_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _prep = null;
        if (_pressedItem is not null)
        {
            SetRowDragPhase(_pressedItem, RowDragPhase.Normal);
            _pressedItem = null;
        }

        if (_active is not null && ReferenceEquals(_active.ListBox, sender))
        {
            EndDragSession(commit: true);
            e.Handled = true;
        }
    }

    private static void List_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_active is not null && ReferenceEquals(_active.ListBox, sender))
        {
            EndDragSession(commit: true);
        }
    }

    private static void EndDragSession(bool commit)
    {
        var session = _active;
        if (session is null)
        {
            return;
        }

        _active = null;

        if (session.EscapeHandler is not null)
        {
            session.HostWindow.PreviewKeyDown -= session.EscapeHandler;
        }

        session.Ghost?.Close();

        var src = session.SourceItem;
        src.BeginAnimation(UIElement.OpacityProperty, null);
        src.Opacity = 1;
        SetIsActiveDragSource(src, false);
        src.ClearValue(FrameworkElement.RenderTransformProperty);
        System.Windows.Controls.Panel.SetZIndex(src, 0);
        _ = Mouse.Capture(null);

        if (Sessions.TryGetValue(session.ListBox, out var s))
        {
            if (!commit && s.OrderSnapshot is { } snap)
            {
                RestoreOrder(session.ListBox, snap);
            }
            else if (commit)
            {
                session.ListBox.RaiseEvent(new RoutedEventArgs(ReorderCompletedEvent, session.ListBox));
            }

            s.OrderSnapshot = null;
        }
    }

    private static Dictionary<object, double> RecordItemTops(ListBox lb)
    {
        var d = new Dictionary<object, double>((IEqualityComparer<object>)ReferenceEqualityComparer.Instance);
        for (var i = 0; i < lb.Items.Count; i++)
        {
            if (lb.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem lbi || lbi.DataContext is null)
            {
                continue;
            }

            d[lbi.DataContext] = lbi.TransformToAncestor(lb).Transform(new Point(0, 0)).Y;
        }

        return d;
    }

    private static void PlayFlipAnimations(ListBox lb, Dictionary<object, double> topsBefore)
    {
        var list = lb.ItemsSource as IList;
        if (list is null)
        {
            return;
        }

        foreach (var kv in topsBefore)
        {
            var dc = kv.Key;
            var oldY = kv.Value;
            var idx = IndexOf(list, dc);
            if (idx < 0)
            {
                continue;
            }

            if (lb.ItemContainerGenerator.ContainerFromIndex(idx) is not ListBoxItem lbi)
            {
                continue;
            }

            var newY = lbi.TransformToAncestor(lb).Transform(new Point(0, 0)).Y;
            var delta = oldY - newY;
            if (Math.Abs(delta) < 0.35)
            {
                continue;
            }

            lbi.RenderTransformOrigin = new Point(0.5, 0.35);
            lbi.BeginAnimation(FrameworkElement.RenderTransformProperty, null);
            var tt = new TranslateTransform(0, delta);
            lbi.RenderTransform = tt;
            var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            anim.Completed += (_, _) =>
            {
                tt.BeginAnimation(TranslateTransform.YProperty, null);
                lbi.ClearValue(FrameworkElement.RenderTransformProperty);
            };
            tt.BeginAnimation(TranslateTransform.YProperty, anim);
        }
    }

    private static void SetRowDragPhase(ListBoxItem item, RowDragPhase phase)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        switch (phase)
        {
            case RowDragPhase.Pressed:
                item.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.92, TimeSpan.FromMilliseconds(75)) { EasingFunction = ease });
                break;
            case RowDragPhase.Normal:
            default:
                item.BeginAnimation(UIElement.OpacityProperty, null);
                item.Opacity = 1;
                break;
        }
    }

    private static void RestoreOrder(ListBox lb, List<object?> snap)
    {
        if (lb.ItemsSource is not IList list || snap.Count != list.Count)
        {
            return;
        }

        list.Clear();
        foreach (var o in snap)
        {
            list.Add(o!);
        }
    }

    private static bool ApplyMoveByItem(IList list, object? dragItem, int insertBefore)
    {
        var from = IndexOf(list, dragItem);
        return ApplyMove(list, from, insertBefore);
    }

    private static int IndexOf(IList list, object? item)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], item))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool ApplyMove(IList list, int from, int insertBefore)
    {
        var n = list.Count;
        if (from < 0 || from >= n)
        {
            return false;
        }

        insertBefore = Math.Clamp(insertBefore, 0, n);
        if (insertBefore == from || insertBefore == from + 1)
        {
            return false;
        }

        var moved = list[from]!;
        list.RemoveAt(from);
        if (insertBefore > from)
        {
            insertBefore--;
        }

        list.Insert(insertBefore, moved);
        return true;
    }

    private static int GetInsertBeforeIndex(ListBox lb, Point mouseInLb)
    {
        for (var i = 0; i < lb.Items.Count; i++)
        {
            if (lb.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem lbi)
            {
                continue;
            }

            var topLeft = lbi.TransformToAncestor(lb).Transform(new Point(0, 0));
            var midY = topLeft.Y + lbi.ActualHeight * 0.5;
            if (mouseInLb.Y < midY)
            {
                return i;
            }
        }

        return lb.Items.Count;
    }

    /// <summary>
    /// Renders the row via DrawingVisual + VisualBrush so scrolled items still snapshot; direct RenderTargetBitmap.Render(row) often comes out empty.
    /// </summary>
    private static ImageBrush CreateRowSnapshotBrush(ListBoxItem source, Size size)
    {
        var dpi = VisualTreeHelper.GetDpi(source);
        var w = Math.Max(1, (int)Math.Ceiling(size.Width * dpi.DpiScaleX));
        var h = Math.Max(1, (int)Math.Ceiling(size.Height * dpi.DpiScaleY));

        var dv = new DrawingVisual();
        using (var ctx = dv.RenderOpen())
        {
            var vb = new VisualBrush(source)
            {
                Stretch = Stretch.Fill,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                TileMode = TileMode.None
            };
            ctx.DrawRectangle(vb, null, new Rect(0, 0, size.Width, size.Height));
        }

        var rtb = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(dv);
        if (rtb.CanFreeze)
        {
            rtb.Freeze();
        }

        return new ImageBrush(rtb) { Stretch = Stretch.Fill };
    }

    private sealed class GhostDragPopup
    {
        private readonly Popup _popup;
        private readonly Vector _grabWin;

        public GhostDragPopup(Window win, ListBoxItem source, MouseEventArgs e, Size size)
        {
            var itemWin = source.TransformToAncestor(win).Transform(new Point(0, 0));
            var mouseWin = e.GetPosition(win);
            _grabWin = mouseWin - itemWin;

            var fill = CreateRowSnapshotBrush(source, size);
            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = size.Width,
                Height = size.Height,
                RadiusX = 8,
                RadiusY = 8,
                Fill = fill,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true,
                Opacity = 1
            };

            _popup = new Popup
            {
                AllowsTransparency = true,
                PlacementTarget = win,
                Placement = PlacementMode.Relative,
                HorizontalOffset = mouseWin.X - _grabWin.X,
                VerticalOffset = mouseWin.Y - _grabWin.Y,
                StaysOpen = true,
                IsHitTestVisible = false,
                Focusable = false,
                PopupAnimation = PopupAnimation.None,
                Child = rect
            };
            _popup.IsOpen = true;
        }

        public void Move(Window win, MouseEventArgs e)
        {
            var m = e.GetPosition(win);
            _popup.HorizontalOffset = m.X - _grabWin.X;
            _popup.VerticalOffset = m.Y - _grabWin.Y;
        }

        public void Close()
        {
            _popup.IsOpen = false;
            _popup.Child = null;
        }
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new();

        bool IEqualityComparer<object>.Equals(object? x, object? y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T t)
            {
                return t;
            }

            d = VisualTreeHelper.GetParent(d);
        }

        return null;
    }
}
