// Copyright (c) 2024-2026 The PulseFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes.Utils;
using FluentFlyoutWPF.Classes.Services;
using FluentFlyoutWPF.Windows;
using Microsoft.Win32;
using NLog;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MessageBox = Wpf.Ui.Controls.MessageBox;

namespace FluentFlyoutWPF.Pages;

public partial class SystemPage : Page
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    public SystemPage()
    {
        InitializeComponent();
        DataContext = SettingsManager.Current;
        UpdateMonitorList();
        var configured = SettingsManager.Current.LyricsApiBaseUrl?.TrimEnd('/') + "/";
        LyricsApiPreset.SelectedIndex = string.Equals(configured, "https://music.loongst.com/", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        LyricsApiCustom.IsEnabled = LyricsApiPreset.SelectedIndex == 1;
        Loaded += async (_, _) => await RefreshNeteaseAccountAsync();
    }

    private async Task RefreshNeteaseAccountAsync()
    {
        var account = await NeteaseMusicService.GetAccountAsync();
        var loggedIn = account is not null || NeteaseMusicService.IsLoggedIn;
        NeteaseAccountName.Text = account?.Name ?? (loggedIn ? "已登录网易云账号" : "未登录");
        NeteaseAccountSummary.Text = loggedIn ? $"登录方式：网易云扫码{(account?.Id is > 0 ? $" · UID {account.Id}" : string.Empty)}" : "登录方式：网易云扫码";
        NeteaseLoginButton.Visibility = loggedIn ? Visibility.Collapsed : Visibility.Visible;
        NeteaseLogoutButton.Visibility = loggedIn ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void NeteaseLoginButton_Click(object sender, RoutedEventArgs e)
    {
        await NeteaseLoginWindow.ShowLoginAsync(Window.GetWindow(this)!);
        await RefreshNeteaseAccountAsync();
    }

    private void NeteaseLogoutButton_Click(object sender, RoutedEventArgs e)
    {
        NeteaseMusicService.ClearLogin();
        _ = RefreshNeteaseAccountAsync();
    }

    private void LyricsApiPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LyricsApiPreset.SelectedItem is not ComboBoxItem item) return;
        if (string.Equals(item.Tag?.ToString(), "custom", StringComparison.OrdinalIgnoreCase))
        {
            LyricsApiCustom.IsEnabled = true;
            return;
        }
        LyricsApiCustom.IsEnabled = false;
        SettingsManager.Current.LyricsApiBaseUrl = item.Tag?.ToString() ?? "https://music.loongst.com/";
    }

    private void PluginsCard_Click(object sender, RoutedEventArgs e) => SettingsWindow.NavigateToPage(typeof(PluginsPage));

    private void StartupSwitch_Click(object sender, RoutedEventArgs e)
    {
        SetStartup(StartupSwitch.IsChecked ?? false);
    }

    private void SetStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            const string appName = "PulseFlyout";
            var executablePath = Environment.ProcessPath;

            if (enable)
            {
                if (File.Exists(executablePath))
                {
                    key.SetValue(appName, executablePath);
                }
                else
                {
                    throw new FileNotFoundException("Application executable not found");
                }
            }
            else
            {
                if (key.GetValue(appName) != null)
                {
                    key.DeleteValue(appName, false);
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox messageBox = new()
            {
                Title = "Error",
                Content = $"Failed to set startup: {ex.Message}",
                CloseButtonText = "OK",
            };

            _ = messageBox.ShowDialogAsync();
        }
    }

    private void StartupHyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void ToggleSwitch_Click(object sender, RoutedEventArgs e)
    {
        bool isChecked = (bool)NIconHideSwitch.IsChecked;

        MainWindow mainWindow = (MainWindow)Application.Current.MainWindow;

        if (!isChecked)
        {
            mainWindow.nIcon.Register();
        }
        else
        {
            mainWindow.nIcon.Unregister();
        }
    }

    private void UpdateMonitorList()
    {
        MonitorUtil.UpdateMonitorList(
            FlyoutSelectedMonitorComboBox,
            () => SettingsManager.Current.FlyoutSelectedMonitor,
            value => SettingsManager.Current.FlyoutSelectedMonitor = value);
    }


    private async void ExportButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var saveFileDialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"PulseFlyout_Settings_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}",
            DefaultExt = ".xml",
            Filter = "XML Files (*.xml)|*.xml|All Files (*.*)|*.*"
        };

        if (saveFileDialog.ShowDialog() == true)
        {
            try
            {
                SettingsManager.SaveSettings(saveFileDialog.FileName);

                Wpf.Ui.Controls.MessageBox messageBox = new()
                {
                    Title = Application.Current.FindResource("ExportSuccessful").ToString(),
                    Content = Application.Current.FindResource("SettingsExportedSuccessfully").ToString(),
                    CloseButtonText = "OK",
                };

                _ = messageBox.ShowDialogAsync();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error exporting settings");

                Wpf.Ui.Controls.MessageBox messageBox = new()
                {
                    Title = Application.Current.FindResource("ExportFailed").ToString(),
                    Content = Application.Current.FindResource("FailedToExportSettings").ToString(),
                    CloseButtonText = "OK",
                };

                _ = messageBox.ShowDialogAsync();
            }
        }
    }

    private async void ImportButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var openFileDialog = new Microsoft.Win32.OpenFileDialog
        {
            DefaultExt = ".xml",
            Filter = "XML Files (*.xml)|*.xml|All Files (*.*)|*.*"
        };

        if (openFileDialog.ShowDialog() == true)
        {
            Wpf.Ui.Controls.MessageBox confirmBox = new()
            {
                Title = Application.Current.FindResource("ImportSettings").ToString(),
                Content = Application.Current.FindResource("ImportSettingsWarning").ToString(),
                CloseButtonText = "No",
                SecondaryButtonText = "Yes",
            };

            var result = await confirmBox.ShowDialogAsync();

            if (result == Wpf.Ui.Controls.MessageBoxResult.Secondary)
            {
                try
                {
                    SettingsManager.RestoreSettings(openFileDialog.FileName);
                    SettingsManager.SaveSettings();

                    Wpf.Ui.Controls.MessageBox messageBox = new()
                    {
                        Title = Application.Current.FindResource("ImportSuccessful").ToString(),
                        Content = Application.Current.FindResource("SettingsImportedSuccessfully").ToString(),
                        CloseButtonText = "OK",
                    };

                    _ = messageBox.ShowDialogAsync();

                    // Restart the application
                    Application.Current.Shutdown();
                    System.Diagnostics.Process.Start(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Error importing settings");

                    Wpf.Ui.Controls.MessageBox messageBox = new()
                    {
                        Title = Application.Current.FindResource("ImportFailed").ToString(),
                        Content = Application.Current.FindResource("FailedToImportSettings").ToString(),
                        CloseButtonText = "OK",
                    };

                    _ = messageBox.ShowDialogAsync();
                }
            }
        }
    }

    private void AppFiltering_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SettingsWindow.NavigateToPage(typeof(AppFilteringPage));
    }

    private void Advanced_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SettingsWindow.NavigateToPage(typeof(AdvancedPage));
    }
}
