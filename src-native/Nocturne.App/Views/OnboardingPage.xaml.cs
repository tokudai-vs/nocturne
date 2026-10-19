using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Nocturne.App.ViewModels;
using Nocturne.Core.Models;

namespace Nocturne.App.Views;

public partial class OnboardingPage : UserControl
{
    private OnboardingViewModel? ViewModel => DataContext as OnboardingViewModel;

    public OnboardingPage()
    {
        InitializeComponent();
    }

    private async void OnConnectServerClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.ConnectToServerAsync();
        }
    }

    private void OnServerUrlKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnConnectServerClick(sender, e);
        }
    }

    private async void OnConnectSavedServerClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ServerConfig server } && ViewModel != null)
        {
            await ViewModel.SwitchToSavedServerAsync(server);
        }
    }

    private void OnRemoveSavedServerClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ServerConfig server } && ViewModel != null)
        {
            ViewModel.RemoveSavedServer(server);
        }
    }

    private async void OnPublicUserClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PublicUser user } && ViewModel != null)
        {
            await ViewModel.SelectUserAsync(user);
            PasswordBox.Focus();
        }
    }

    private void OnSwitchToManualClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            ViewModel.IsManualMode = true;
        }
    }

    private async void OnSignInClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            var username = ViewModel.IsManualMode
                ? ViewModel.ManualUsername
                : ViewModel.SelectedUser?.Name ?? string.Empty;

            var password = PasswordBox.Password;
            await ViewModel.LoginAsync(username, password);
        }
    }

    private void OnPasswordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnSignInClick(sender, e);
        }
    }

    private void OnChangeServerClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            ViewModel.GoBackToServers();
            PasswordBox.Password = string.Empty;
        }
    }

    private async void OnSignInWithEmbyConnectClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.StartEmbyConnectPinFlowAsync();
        }
    }

    private async void OnSignInWithEmbyConnectForServerClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.StartEmbyConnectPinFlowAsync(ViewModel.ServerUrl);
        }
    }

    private void OnOpenPinBrowserClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.OpenPinBrowser();
    }

    private void OnCancelEmbyConnectClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.CancelEmbyConnect();
    }

    private async void OnConnectEmbyConnectServerClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ConnectServer server } && ViewModel != null)
        {
            await ViewModel.ConnectToEmbyConnectServerAsync(server);
        }
    }
}
