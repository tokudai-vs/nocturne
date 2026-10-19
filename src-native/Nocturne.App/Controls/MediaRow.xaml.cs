using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using Nocturne.Core.Models;

namespace Nocturne.App.Controls;

public partial class MediaRow : UserControl
{
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

    public event RoutedEventHandler ItemClick
    {
        add => AddHandler(ItemClickEvent, value);
        remove => RemoveHandler(ItemClickEvent, value);
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
    }

    private void OnPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            if (e.Delta < 0)
                sv.ScrollToHorizontalOffset(sv.HorizontalOffset + 140);
            else if (e.Delta > 0)
                sv.ScrollToHorizontalOffset(Math.Max(0, sv.HorizontalOffset - 140));
            e.Handled = true;
        }
    }

    private void OnRowMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        LeftScrollButton.Opacity = 1;
        RightScrollButton.Opacity = 1;
    }

    private void OnRowMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        LeftScrollButton.Opacity = 0;
        RightScrollButton.Opacity = 0;
    }

    private void OnScrollLeftClick(object sender, RoutedEventArgs e)
    {
        RowScrollViewer.ScrollToHorizontalOffset(Math.Max(0, RowScrollViewer.HorizontalOffset - 500));
    }

    private void OnScrollRightClick(object sender, RoutedEventArgs e)
    {
        RowScrollViewer.ScrollToHorizontalOffset(RowScrollViewer.HorizontalOffset + 500);
    }

    private void OnMediaCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is MediaCard card && card.DataContext is EmbyItem item)
        {
            RaiseEvent(new ItemClickEventArgs(ItemClickEvent, this, item));
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
