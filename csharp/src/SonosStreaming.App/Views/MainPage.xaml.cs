using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SonosStreaming.App.ViewModels;
using SonosStreaming.Core.Audio;
using SonosStreaming.Core.State;

namespace SonosStreaming.App.Views;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel { get; }

    public MainPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<MainViewModel>();
        DataContext = ViewModel;
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.RescanCommand.ExecuteAsync(null);
    }

    private void WholeSystemRadio_Checked(object sender, RoutedEventArgs e)
    {
        ViewModel.IsProcessSourceSelected = false;
        ViewModel.SelectSourceCommand.Execute(AudioSourceSelection.WholeSystem);
    }

    private void ProcessRadio_Checked(object sender, RoutedEventArgs e)
    {
        ViewModel.IsProcessSourceSelected = true;
        ViewModel.SelectSourceCommand.Execute(AudioSourceSelection.Process);
    }

    private void ErrorInfoBar_CloseButtonClick(InfoBar sender, object args)
    {
        ViewModel.IsErrorVisible = false;
    }

    private void QuitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        App.Current.RequestQuit();
    }

    private void ThemeSystemMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ThemePreference = ThemePreference.System;
    }

    private void ThemeLightMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ThemePreference = ThemePreference.Light;
    }

    private void ThemeDarkMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ThemePreference = ThemePreference.Dark;
    }

    private async void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "About RoomRelay",
            Content = $"{ViewModel.AppVersionLabel}\nby guicn555\n\nStreams Windows audio to Sonos speakers over your local network.",
            CloseButtonText = "Close",
            XamlRoot = XamlRoot,
        };

        await dialog.ShowAsync();
    }
}
