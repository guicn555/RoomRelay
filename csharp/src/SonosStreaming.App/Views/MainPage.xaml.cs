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

    private void OpenLogsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenLogsFolderCommand.Execute(null);
    }

    private async void CreateDiagnosticsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.CreateDiagnosticsPackageCommand.ExecuteAsync(null);
    }

    private async void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "About RoomRelay",
            Content = $"{ViewModel.AppVersionLabel}\n\nStreams Windows audio to Sonos speakers over your local network.",
            CloseButtonText = "Close",
            XamlRoot = XamlRoot,
        };

        await dialog.ShowAsync();
    }
}
