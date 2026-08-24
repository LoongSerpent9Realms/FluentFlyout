// Copyright (c) 2024-2026 The PulseFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyout.Classes;
using FluentFlyout.Classes.Utils;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace FluentFlyoutWPF.Pages;

public partial class MediaFlyoutPage : Page
{
    public MediaFlyoutPage()
    {
        InitializeComponent();
        DataContext = SettingsManager.Current;
        PopulatePreferredMediaApps();
    }

    private void PopulatePreferredMediaApps()
    {
        var mainWindow = Application.Current.MainWindow as MainWindow;
        var settings = SettingsManager.Current;
        var apps = mainWindow?.mediaManager.CurrentMediaSessions.Values
            .Select(session =>
            {
                var appId = session.Id ?? string.Empty;
                var appName = MediaPlayerData.GetAndCacheMediaPlayerData(appId).Item1 ?? appId;
                return appName;
            })
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        var ordered = settings.PreferredMediaAppOrder ??= [];
        var current = ordered.ToList();
        current.AddRange(apps.Where(app => !current.Contains(app, StringComparer.OrdinalIgnoreCase)));
        ordered.Clear();
        foreach (var app in current) ordered.Add(app);
        PreferredMediaAppListBox.ItemsSource = null;
        PreferredMediaAppListBox.ItemsSource = ordered;
    }

    private void MovePreferredMediaAppUp_Click(object sender, RoutedEventArgs e) => MovePreferredMediaApp(-1);
    private void MovePreferredMediaAppDown_Click(object sender, RoutedEventArgs e) => MovePreferredMediaApp(1);

    private void MovePreferredMediaApp(int offset)
    {
        var list = SettingsManager.Current.PreferredMediaAppOrder;
        var index = PreferredMediaAppListBox.SelectedIndex;
        var target = index + offset;
        if (index < 0 || target < 0 || target >= list.Count) return;
        list.Move(index, target);
        PreferredMediaAppListBox.SelectedIndex = target;
        SettingsManager.SaveSettings();
        (Application.Current.MainWindow as MainWindow)?.RefreshFilteredMedia();
    }
}
