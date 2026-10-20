using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using Nocturne.Core.Models;

namespace Nocturne.App.Controls;

public partial class MediaRow : UserControl
{
    public static readonly DependencyProperty IsHorizontalProperty =
        DependencyProperty.Register(nameof(IsHorizontal), typeof(bool), typeof(MediaRow),
            new PropertyMetadata(false, (d, e) => ((MediaRow)d).UpdateItemTemplate()));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(MediaRow),
            new PropertyMetadata(string.Empty, (d, e) => ((MediaRow)d).HeaderTitle.Text = (string)e.NewValue));

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(MediaRow),
            new PropertyMetadata(null, OnItemsSourceChanged));

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var row = (MediaRow)d;
        row.ItemsHost.ItemsSource = (IEnumerable)e.NewValue;

        if (e.OldValue is INotifyCollectionChanged oldColl)
        {
            oldColl.CollectionChanged -= row.OnCollectionChanged;
        }

        if (e.NewValue is INotifyCollectionChanged newColl)
        {
            newColl.CollectionChanged += row.OnCollectionChanged;
        }

        row.UpdateVisibility();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        var hasAny = false;
        if (ItemsSource is ICollection col)
        {
            hasAny = col.Count > 0;
        }
        else if (ItemsSource != null)
        {
            var enumerator = ItemsSource.GetEnumerator();
            hasAny = enumerator.MoveNext();
        }
        Visibility = hasAny ? Visibility.Visible : Visibility.Collapsed;
    }

    public static readonly RoutedEvent ItemClickEvent =
        EventManager.RegisterRoutedEvent(nameof(ItemClick), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(MediaRow));

    public static readonly RoutedEvent PlayClickEvent =
        EventManager.RegisterRoutedEvent(nameof(PlayClick), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(MediaRow));

    public static readonly RoutedEvent ShowClickEvent =
        EventManager.RegisterRoutedEvent(nameof(ShowClick), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(MediaRow));

    public static readonly RoutedEvent EpisodeClickEvent =
        EventManager.RegisterRoutedEvent(nameof(EpisodeClick), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(MediaRow));

    public event RoutedEventHandler ItemClick
    {
        add => AddHandler(ItemClickEvent, value);
        remove => RemoveHandler(ItemClickEvent, value);
    }

    public event RoutedEventHandler PlayClick
    {
        add => AddHandler(PlayClickEvent, value);
        remove => RemoveHandler(PlayClickEvent, value);
    }

    public event RoutedEventHandler ShowClick
    {
        add => AddHandler(ShowClickEvent, value);
        remove => RemoveHandler(ShowClickEvent, value);
    }

    public event RoutedEventHandler EpisodeClick
    {
        add => AddHandler(EpisodeClickEvent, value);
        remove => RemoveHandler(EpisodeClickEvent, value);
    }

    public bool IsHorizontal
    {
        get => (bool)GetValue(IsHorizontalProperty);
        set => SetValue(IsHorizontalProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public IEnumerable ItemsSource
    {
        get => (IEnumerable)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public MediaRow()
    {
        InitializeComponent();
        Loaded += (s, e) => UpdateItemTemplate();
    }

    private void UpdateItemTemplate()
    {
        var templateName = IsHorizontal ? "HorizontalCardTemplate" : "VerticalCardTemplate";
        if (Resources.Contains(templateName))
        {
            ItemsHost.ItemTemplate = (DataTemplate)Resources[templateName];
        }
    }

    private void OnPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (!e.Handled && sender is UIElement element)
        {
            e.Handled = true;

            var eventArg = new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = sender
            };

            var parent = System.Windows.Media.VisualTreeHelper.GetParent(element) as UIElement
                         ?? (element as FrameworkElement)?.Parent as UIElement;
            parent?.RaiseEvent(eventArg);
        }
    }

    private bool _isHovered;

    private void UpdateScrollButtonVisibility()
    {
        if (RowScrollViewer == null) return;
        var scrollable = RowScrollViewer.ScrollableWidth > 5;
        if (!scrollable || !_isHovered)
        {
            LeftScrollButton.Opacity = 0;
            RightScrollButton.Opacity = 0;
            LeftScrim.Opacity = 0;
            RightScrim.Opacity = 0;
            return;
        }

        var canScrollLeft = RowScrollViewer.HorizontalOffset > 2;
        var canScrollRight = RowScrollViewer.HorizontalOffset < (RowScrollViewer.ScrollableWidth - 2);

        LeftScrollButton.Opacity = canScrollLeft ? 1 : 0;
        LeftScrim.Opacity = canScrollLeft ? 1 : 0;
        RightScrollButton.Opacity = canScrollRight ? 1 : 0;
        RightScrim.Opacity = canScrollRight ? 1 : 0;
    }

    private void OnRowMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isHovered = true;
        UpdateScrollButtonVisibility();
    }

    private void OnRowMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isHovered = false;
        LeftScrollButton.Opacity = 0;
        RightScrollButton.Opacity = 0;
        LeftScrim.Opacity = 0;
        RightScrim.Opacity = 0;
    }

    private System.Windows.Threading.DispatcherTimer? _scrollTimer;
    private double _startOffset;
    private double _targetOffset;
    private DateTime _animationStartTime;
    private readonly TimeSpan _animationDuration = TimeSpan.FromMilliseconds(280);

    private void SmoothScrollBy(double delta)
    {
        var current = _scrollTimer != null && _scrollTimer.IsEnabled ? _targetOffset : RowScrollViewer.HorizontalOffset;
        var target = Math.Max(0, Math.Min(RowScrollViewer.ScrollableWidth, current + delta));
        _startOffset = RowScrollViewer.HorizontalOffset;
        _targetOffset = target;
        _animationStartTime = DateTime.UtcNow;

        if (_scrollTimer == null)
        {
            _scrollTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _scrollTimer.Tick += OnScrollTick;
        }
        _scrollTimer.Start();
    }

    private void OnScrollTick(object? sender, EventArgs e)
    {
        var elapsed = (DateTime.UtcNow - _animationStartTime).TotalMilliseconds;
        var progress = Math.Clamp(elapsed / _animationDuration.TotalMilliseconds, 0.0, 1.0);
        var ease = 1.0 - Math.Pow(1.0 - progress, 3);
        var newOffset = _startOffset + (_targetOffset - _startOffset) * ease;
        RowScrollViewer.ScrollToHorizontalOffset(newOffset);
        UpdateScrollButtonVisibility();

        if (progress >= 1.0)
        {
            _scrollTimer?.Stop();
        }
    }

    private void OnScrollLeftClick(object sender, RoutedEventArgs e)
    {
        SmoothScrollBy(-500);
    }

    private void OnScrollRightClick(object sender, RoutedEventArgs e)
    {
        SmoothScrollBy(500);
    }

    private void OnMediaCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is MediaCard card && card.DataContext is EmbyItem item)
        {
            RaiseEvent(new ItemClickEventArgs(ItemClickEvent, this, item));
        }
    }

    private void OnContinueWatchingPlayClick(object sender, RoutedEventArgs e)
    {
        if (e is ItemClickEventArgs args)
        {
            RaiseEvent(new ItemClickEventArgs(PlayClickEvent, this, args.Item));
        }
    }

    private void OnContinueWatchingShowClick(object sender, RoutedEventArgs e)
    {
        if (e is ItemClickEventArgs args)
        {
            RaiseEvent(new ItemClickEventArgs(ShowClickEvent, this, args.Item));
        }
    }

    private void OnContinueWatchingEpisodeClick(object sender, RoutedEventArgs e)
    {
        if (e is ItemClickEventArgs args)
        {
            RaiseEvent(new ItemClickEventArgs(EpisodeClickEvent, this, args.Item));
        }
    }
}

public class ItemClickEventArgs : RoutedEventArgs
{
    public EmbyItem Item { get; }

    public ItemClickEventArgs(RoutedEvent routedEvent, object source, EmbyItem item)
        : base(routedEvent, source)
    {
        Item = item;
    }
}
