// Copyright (c) 2024-2026 The PulseFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyoutWPF.Classes.Services;
using MicaWPF.Controls;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

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
        var token = _cancellation.Token;
        NeteaseMusicService.NeteaseQrCode? qr;
        try { qr = await NeteaseMusicService.CreateQrCodeAsync(token); }
        catch (OperationCanceledException) { return; }
        if (qr is null)
        {
            StatusText.Text = "二维码生成失败，请稍后重试";
            return;
        }

        QrImage.Source = DecodeQrImage(qr.Base64Image);
        while (!token.IsCancellationRequested)
        {
            NeteaseMusicService.NeteaseQrLoginResult result;
            try { result = await NeteaseMusicService.CheckQrCodeAsync(qr.Key, token); }
            catch (OperationCanceledException) { return; }
            switch (result.Status)
            {
                case NeteaseMusicService.NeteaseQrLoginStatus.WaitingForScan:
                    StatusText.Text = "请使用网易云音乐扫码";
                    break;
                case NeteaseMusicService.NeteaseQrLoginStatus.WaitingForConfirmation:
                    StatusText.Text = "已扫码，请在手机上确认登录";
                    break;
                case NeteaseMusicService.NeteaseQrLoginStatus.Authorized:
                    StatusText.Text = "登录成功";
                    try { await NeteaseMusicService.WarmLikeListAsync(token); }
                    catch (OperationCanceledException) { return; }
                    _completion.TrySetResult(true);
                    await Task.Delay(450);
                    Close();
                    return;
                case NeteaseMusicService.NeteaseQrLoginStatus.Expired:
                    StatusText.Text = "二维码已过期，请关闭后重试";
                    return;
            }
            try { await Task.Delay(3000, token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static BitmapImage? DecodeQrImage(string value)
    {
        try
        {
            var comma = value.IndexOf(',');
            var bytes = Convert.FromBase64String(comma >= 0 ? value[(comma + 1)..] : value);
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
