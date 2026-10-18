using System.Windows;
using Wpf.Ui.Controls;
using Nocturne.App.ViewModels;

namespace Nocturne.App;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = _viewModel;

        InitializeComponent();

        Loaded += async (s, e) =>
        {
            try
            {
                await _viewModel.InitializeAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Main] Init error: {ex.Message}");
            }
        };
    }

    private void OnNavSelectionChanged(NavigationView sender, RoutedEventArgs args)
    {
        if (sender.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            switch (tag)
            {
                case "home":
                    _viewModel.NavigateToHome();
                    break;
                case "library":
                    _viewModel.NavigateToLibrary();
                    break;
                case "analytics":
                    _viewModel.NavigateToAnalytics();
                    break;
                case "settings":
                    _viewModel.NavigateToSettings();
                    break;
            }
        }
    }
}
