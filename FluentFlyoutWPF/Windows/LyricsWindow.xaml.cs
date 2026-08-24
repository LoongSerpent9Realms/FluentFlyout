// Copyright (c) 2024-2026 The PulseFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Threading;
using System.Windows.Interop;
using FluentFlyout.Classes;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Classes.Services;
using FluentFlyout.Classes.Settings;
using static FluentFlyout.Classes.NativeMethods;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FluentFlyoutWPF.Windows;

public partial class LyricsWindow : Window
{
    private string _title;
    private string _artist;
    private LyricsService.LyricsTrack? _track;
    private Func<TimeSpan>? _getPosition;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _unlockTimer;
    private string _lastCurrentLine = string.Empty;
    private bool _leftButtonDown;
    private DateTime _lastClickUtc = DateTime.MinValue;

    public LyricsWindow(string title, string artist, LyricsService.LyricsTrack? track, Func<TimeSpan>? getPosition = null)
    {
        InitializeComponent();
        _title = title;
        _artist = artist;
        _track = track;
        _getPosition = getPosition;
        SettingsManager.Current.PropertyChanged += Settings_PropertyChanged;
        ApplyTheme(SettingsManager.Current.DesktopLyricsTheme);
        WindowHelper.SetNoActivate(this);
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_LAYERED;
            if (SettingsManager.Current.DesktopLyricsClickThrough) style |= WS_EX_TRANSPARENT;
            SetWindowLong(hwnd, GWL_EXSTYLE, style);
        };
        SongTitleText.Text = title;
        SongArtistText.Text = artist;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _timer.Tick += (_, _) => RefreshLine();
        _timer.Start();
        _unlockTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
        _unlockTimer.Tick += (_, _) => Detect穿透双击();
        _unlockTimer.Start();
        Loaded += (_, _) =>
        {
            Opacity = 0;
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new QuadraticEase() };
            BeginAnimation(OpacityProperty, fade);
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _unlockTimer.Stop();
            SettingsManager.Current.PropertyChanged -= Settings_PropertyChanged;
        };
        RefreshLine();
    }

    private void RefreshLine()
    {
        var position = _getPosition?.Invoke() ?? TimeSpan.Zero;
        var (current, next) = _track?.GetCurrentAndNextLines(position) ?? ($"{_title}\n{_artist}", "");
        CurrentLineText.Text = current;
        NextLineText.Text = next;
        if (!string.Equals(current, _lastCurrentLine, StringComparison.Ordinal))
        {
            _lastCurrentLine = current;
            CurrentLineOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
            CurrentLineText.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(220)));
            NextLineOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(4, 0, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
        }
    }

    public void UpdateTrack(string title, string artist, LyricsService.LyricsTrack? track, Func<TimeSpan>? getPosition)
    {
        _title = title;
        _artist = artist;
        _track = track;
        _getPosition = getPosition;
        SongTitleText.Text = title;
        SongArtistText.Text = artist;
        RefreshLine();
    }

    private void WindowDrag_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    private void LyricsBorder_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!SettingsManager.Current.DesktopLyricsClickThrough)
        {
            LockButton.Visibility = Visibility.Visible;
            CloseButton.Visibility = Visibility.Visible;
        }
    }

    private void LyricsBorder_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        LockButton.Visibility = Visibility.Collapsed;
        CloseButton.Visibility = Visibility.Collapsed;
    }

    private void LockButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsManager.Current.DesktopLyricsClickThrough = true;
        SetMouseThrough(true);
        LockButton.Visibility = Visibility.Collapsed;
    }

    private void SetMouseThrough(bool enabled)
    {
        if (!IsInitialized) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_LAYERED;
        if (enabled) style |= WS_EX_TRANSPARENT;
        else style &= ~WS_EX_TRANSPARENT;
        SetWindowLong(hwnd, GWL_EXSTYLE, style);
    }

    private void Detect穿透双击()
    {
        if (!SettingsManager.Current.DesktopLyricsClickThrough) return;
        if (!GetCursorPos(out var cursor)) return;
        var bounds = WindowHelper.GetPlacement(this);
        var inside = cursor.X >= bounds.Left && cursor.X <= bounds.Right
            && cursor.Y >= bounds.Top && cursor.Y <= bounds.Bottom;
        var pressed = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        if (pressed && !_leftButtonDown && inside)
        {
            var now = DateTime.UtcNow;
            if (now - _lastClickUtc <= TimeSpan.FromMilliseconds(550))
            {
                SettingsManager.Current.DesktopLyricsClickThrough = false;
                SetMouseThrough(false);
                LockButton.Visibility = Visibility.Visible;
                CloseButton.Visibility = Visibility.Visible;
                _lastClickUtc = DateTime.MinValue;
            }
            else
            {
                _lastClickUtc = now;
            }
        }
        _leftButtonDown = pressed;
    }

    private void Settings_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsManager.Current.DesktopLyricsTheme))
        {
            Dispatcher.Invoke(() => ApplyTheme(SettingsManager.Current.DesktopLyricsTheme));
            return;
        }
        if (e.PropertyName != nameof(SettingsManager.Current.DesktopLyricsClickThrough)) return;
        Dispatcher.Invoke(() =>
        {
            SetMouseThrough(SettingsManager.Current.DesktopLyricsClickThrough);
            LockButton.Visibility = SettingsManager.Current.DesktopLyricsClickThrough ? Visibility.Collapsed : Visibility.Visible;
            CloseButton.Visibility = SettingsManager.Current.DesktopLyricsClickThrough ? Visibility.Collapsed : Visibility.Visible;
        });
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        // Closing the window also disables automatic reopening until the user
        // explicitly enables desktop lyrics again from settings.
        SettingsManager.Current.DesktopLyricsEnabled = false;
        Close();
    }

    private void ApplyTheme(int theme)
    {
        var palette = theme switch
        {
            1 => (Surface: "#F2F7FC", Border: "#5590A4B8", Title: "#17212B", Artist: "#882C3E50", Current: "#17212B", Next: "#8832475A", Divider: "#334A6278"),
            2 => (Surface: "#E9141024", Border: "#887E5CFF", Title: "#FFFFFFFF", Artist: "#B8E7DEFF", Current: "#FFFFD166", Next: "#A8FFFFFF", Divider: "#557E5CFF"),
            3 => (Surface: "#E9000000", Border: "#66FFFFFF", Title: "#FFFFFFFF", Artist: "#99999999", Current: "#FFFFFFFF", Next: "#77777777", Divider: "#33333333"),
            _ => (Surface: "#E91B1B1B", Border: "#55FFFFFF", Title: "#FFFFFFFF", Artist: "#B8FFFFFF", Current: "#FFFFFFFF", Next: "#99FFFFFF", Divider: "#44FFFFFF")
        };
        LyricsSurface.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Surface));
        LyricsSurface.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Border));
        SongTitleText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Title));
        SongArtistText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Artist));
        CurrentLineText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Current));
        NextLineText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Next));
    }
}
