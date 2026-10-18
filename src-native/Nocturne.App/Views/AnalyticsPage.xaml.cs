using System.Windows.Controls;
using Nocturne.App.ViewModels;

namespace Nocturne.App.Views;

public partial class AnalyticsPage : UserControl
{
    public AnalyticsPage()
    {
        InitializeComponent();
        Loaded += async (s, e) =>
        {
            if (DataContext is AnalyticsViewModel vm)
            {
                await vm.LoadStatsAsync();
            }
        };
    }
}
