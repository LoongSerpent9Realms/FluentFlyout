using System.Windows.Controls;
using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF;
using System.Windows;

namespace FluentFlyoutWPF.Pages;

public partial class DesktopLyricsPage : Page
{
    public DesktopLyricsPage()
    {
        InitializeComponent();
        DataContext = SettingsManager.Current;
    }

    private async void OpenLyrics_Click(object sender, RoutedEventArgs e)
    {
        if (MainWindow.Current is not null)
            await MainWindow.Current.ShowLyricsForActiveSessionAsync();
    }
}
