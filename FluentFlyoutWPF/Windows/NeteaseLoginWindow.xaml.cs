// Copyright (c) 2024-2026 The PulseFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyoutWPF.Classes.Services;
using MicaWPF.Controls;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace FluentFlyoutWPF.Windows;

public partial class NeteaseLoginWindow : MicaWindow
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource<bool> _completion = new();

    private NeteaseLoginWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await BeginLoginAsync();
        Closed += (_, _) =>
        {
            _cancellation.Cancel();
            _cancellation.Dispose();
            _completion.TrySetResult(NeteaseMusicService.IsLoggedIn);
        };
    }

    public static async Task<bool> ShowLoginAsync(Window owner)
    {
        if (NeteaseMusicService.IsLoggedIn) return true;
        var window = new NeteaseLoginWindow { Owner = owner };
        window.ShowDialog();
        return await window._completion.Task;
    }

    private async Task BeginLoginAsync()
    {
        try
        {
            await LoginBrowser.EnsureCoreWebView2Async();
            await LoginBrowser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
                (() => {
                    let sentCookie = '';
                    setInterval(() => {
                        const cookie = localStorage.getItem('cookie');
                        if (cookie && cookie !== sentCookie) {
                            sentCookie = cookie;
                            chrome.webview.postMessage(cookie);
                        }
                    }, 500);
                })();");
            LoginBrowser.CoreWebView2.WebMessageReceived += LoginBrowser_WebMessageReceived;
            LoginBrowser.CoreWebView2.Navigate("https://music.loongst.com/qrlogin.html");
        }
        catch (Exception)
        {
            StatusText.Text = "无法加载网易云登录页面，请确认已安装 Microsoft Edge WebView2 Runtime";
            StatusText.Visibility = Visibility.Visible;
        }
    }

    private async void LoginBrowser_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var cookie = e.TryGetWebMessageAsString();
        if (string.IsNullOrWhiteSpace(cookie) || _cancellation.IsCancellationRequested) return;
        NeteaseMusicService.SaveLoginCookie(cookie);
        try { await NeteaseMusicService.WarmLikeListAsync(_cancellation.Token); }
        catch (OperationCanceledException) { return; }
        _completion.TrySetResult(true);
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
