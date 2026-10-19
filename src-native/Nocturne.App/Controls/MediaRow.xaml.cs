using System.Collections;
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
            new PropertyMetadata(null, (d, e) => ((MediaRow)d).ItemsHost.ItemsSource = (IEnumerable)e.NewValue));

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
