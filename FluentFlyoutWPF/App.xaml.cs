// Copyright (c) 2024-2026 The PulseFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes;
using FluentFlyoutWPF.Classes.Plugins;
using Microsoft.Toolkit.Uwp.Notifications;
using System.Runtime.InteropServices;
using System.Windows;

namespace FluentFlyoutWPF;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        await PluginManager.Current.LoadAsync();
        Exit += async (_, _) => await PluginManager.Current.DisposeAsync();
        // log unhandled exceptions before crashing
        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            NLog.LogManager.GetCurrentClassLogger().Error(args.ExceptionObject as Exception, "Unhandled exception occurred");
            NLog.LogManager.Flush(); // Ensure logs are written before application dies
        };

        // SMTC sessions can disappear between a callback and a property read. The
        // WinRT projection reports that race as InvalidOperationException on the
        // dispatcher thread; treat it as a transient media update failure instead
        // of terminating the whole WPF process.
        DispatcherUnhandledException += (_, args) =>
        {
            if (args.Exception is InvalidOperationException or COMException)
            {
                NLog.LogManager.GetCurrentClassLogger().Warn(args.Exception,
                    "Ignoring transient WinRT media-session exception");
                args.Handled = true;
            }
        };

        // Register AUMID for toast notifications
        ToastNotificationManagerCompat.OnActivated += Notifications.HandleNotificationActivation;

        base.OnStartup(e);
    }
}
