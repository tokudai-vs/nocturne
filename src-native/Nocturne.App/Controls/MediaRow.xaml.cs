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
        EventManager.RegisterRoutedEvent(nameof(ItemClick), RoutingStrategy.Bubble, typeof(EventHandler<EmbyItem>), typeof(MediaRow));

    public event EventHandler<EmbyItem> ItemClick
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

    private void OnMediaCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is MediaCard card && card.DataContext is EmbyItem item)
        {
            RaiseEvent(new RoutedEventArgs(ItemClickEvent, item));
        }
    }
}
