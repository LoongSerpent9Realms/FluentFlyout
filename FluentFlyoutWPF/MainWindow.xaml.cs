// Copyright (c) 2024-2026 The PulseFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes;
using FluentFlyout.Classes.Settings;
using FluentFlyout.Classes.Utils;
using FluentFlyout.Controls;
using FluentFlyout.Windows;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Classes.Services;
using FluentFlyoutWPF.Classes.Utils;
using FluentFlyoutWPF.Classes.Plugins;
using FluentFlyout.PluginApi;
using FluentFlyoutWPF.ViewModels;
using FluentFlyoutWPF.Windows;
using MicaWPF.Controls;
using MicaWPF.Core.Extensions;
using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Text.RegularExpressions;
using Windows.ApplicationModel;
using Windows.Media.Control;
using Windows.Storage.Streams;
using static FluentFlyout.Classes.NativeMethods;
using static FluentFlyoutWPF.Classes.Utils.MonitorUtil;
using static WindowsMediaController.MediaManager;


namespace FluentFlyoutWPF;

public partial class MainWindow : MicaWindow
{
    public static MainWindow? Current { get; private set; }
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private int WM_TASKBARCREATED, WM_SHELLHOOK;

    private IntPtr _hookId = IntPtr.Zero;
    private LowLevelKeyboardProc _hookProc;

    private CancellationTokenSource cts; // to close the flyout after a certain time
    private LyricsWindow? _lyricsWindow;
#if DEBUG
    private LyricsDebugWindow? _lyricsDebugWindow;
#endif
    private readonly Dictionary<string, LyricsService.LyricsTrack?> _lyricsCache = new();
    private string? _lyricsCacheKey;
    private LyricsService.LyricsTrack? _activeLyricsTrack;
    private string? _activeLyricsTitle;
    private readonly DispatcherTimer _lyricsDisplayTimer;
    private DateTime _lastLyricsAttemptUtc;
    private CancellationTokenSource? _lyricsLoadCts;
    private string _lastAccentMediaKey = string.Empty;
    private string _animatedLyricsText = string.Empty;
    private int _animatedLyricsIndex;
    private DispatcherTimer? _lyricsWordTimer;
    private GlobalSystemMediaTransportControlsSessionManager? _nativeSmtcManager;
    // Some players (notably NetEase Cloud Music) expose a zeroed SMTC timeline.
    // Keep a local monotonic estimate as a fallback so synced lyrics can advance.
    private string? _estimatedPositionKey;
    private TimeSpan _estimatedPosition;
    private DateTime _estimatedPositionUpdatedUtc;
    private string _lastNeteaseLikeEvent = "尚未点击红心";
    private DateTime _lastNeteaseLikeEventUtc;
    private string _neteaseLikeSongKey = string.Empty;
    private CancellationTokenSource? _neteaseLikeStateCts;
    private long _lastFlyoutTime = 0;

    public readonly WindowsMediaController.MediaManager mediaManager = new();

    // for detecting changes in settings (lazy way)
    private int _position = SettingsManager.Current.Position;
    private bool _layout = SettingsManager.Current.CompactLayout;
    private bool _repeatEnabled = SettingsManager.Current.RepeatEnabled;
    private bool _shuffleEnabled = SettingsManager.Current.ShuffleEnabled;
    private bool _playerInfoEnabled = SettingsManager.Current.PlayerInfoEnabled;
    private bool _centerTitleArtist = SettingsManager.Current.CenterTitleArtist;
    private bool _seekBarEnabled = SettingsManager.Current.SeekbarEnabled;
    private bool _alwaysDisplay = SettingsManager.Current.MediaFlyoutAlwaysDisplay;
    private bool _mediaSessionSupportsSeekbar = false; // default off to handle initialization
    private bool _acrylicEnabled = false; // default off to handle initialization
    private int _themeOption = SettingsManager.Current.AppTheme;

    static Mutex singleton = new Mutex(true, "PulseFlyout"); // to prevent multiple instances of the app
    private NextUpWindow? nextUpWindow = null; // to prevent multiple instances of NextUpWindow
    private string currentTitle = ""; // to prevent NextUpWindow from showing the same song

    private readonly int _seekbarUpdateInterval = 300;
    private readonly Timer _positionTimer;
    private bool _isActive;
    private bool _isDragging;
    private bool _isHiding = true;

    private LockWindow? lockWindow;
    private DateTime _lastSelfUpdateTimestamp = DateTime.MinValue;

    internal TaskbarWindow? taskbarWindow;

    private VolumeMixerWindow? volumeMixerWindow;

    private readonly DispatcherTimer _displayRefreshTimer;
    private readonly DispatcherTimer _mediaSelectionTimer;
    private string _pendingDisplayRefreshReason = "Unknown";
    private bool _displayRefreshInProgress;
    private bool _isCleaningUp;
    private bool _fullscreenLyricsActive;
    private bool _lyricsOpenedByFullscreen;

    internal static volatile bool ExplorerRestarting = false;

    public MainWindow()
    {
        DataContext = SettingsManager.Current;
        SettingsManager.Current.PropertyChanged += SettingsManager_PropertyChanged;
        WindowHelper.SetNoActivate(this); // prevents some fullscreen apps from minimizing
        InitializeComponent();
        BuildPluginTrayItems();
        Current = this;
#if DEBUG
        Loaded += (_, _) => LyricsDebug_Click(this, new RoutedEventArgs());
#else
        NotifyIconLyricsDebug.Visibility = Visibility.Collapsed;
#endif
        WindowHelper.SetTopmost(this); // more prevention of fullscreen apps minimizing

        _displayRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1000)
        };
        _displayRefreshTimer.Tick += DisplayRefreshTimer_Tick;
        _mediaSelectionTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _mediaSelectionTimer.Tick += (_, _) => RefreshActiveMediaSelection();
        _mediaSelectionTimer.Start();
        _lyricsDisplayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _lyricsDisplayTimer.Tick += async (_, _) =>
        {
            await UpdateFullscreenLyricsAsync();
            UpdateDesktopLyricsWindow();
            UpdateLyricsDisplay();
            if (_activeLyricsTrack is null && DateTime.UtcNow - _lastLyricsAttemptUtc > TimeSpan.FromSeconds(5)
                && GetActiveMediaSession() is { } retrySession)
            {
                var retryInfo = TryGetMediaProperties(retrySession.ControlSession);
                if (retryInfo is not null) _ = PreloadLyricsAsync(retrySession, retryInfo.Title, retryInfo.Artist);
            }
        };
        _lyricsDisplayTimer.Start();
        _ = InitializeNativeSmtcAsync();

        if (!singleton.WaitOne(TimeSpan.Zero, true)) // if another instance is already running, close this one
        {
            // Signal the existing instance to open settings
            Task.Run(() =>
            {
                try
                {
                    using (EventWaitHandle settingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "FluentFlyout_OpenSettings"))
                    {
                        settingsEvent.Set();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Failed to signal existing instance");
                }
            });

            Environment.Exit(0);
        }

        Logger.Info("Starting PulseFlyout MainWindow");

        // in the existing instance, listen for the signal to open settings
        Task.Run(() =>
        {
            try
            {
                using (EventWaitHandle settingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "FluentFlyout_OpenSettings"))
                {
                    while (true)
                    {
                        settingsEvent.WaitOne();
                        Application.Current.Dispatcher.Invoke(() => { SettingsWindow.ShowInstance(); });
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Settings event listener error");
            }
        });

        try
        {
            SettingsManager.RestoreSettings();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to restore settings: {ex.Message}");
            Logger.Error(ex, "Failed to restore settings");
        }

        // RestoreSettings may replace SettingsManager.Current instance, so rebind DataContext.
        DataContext = SettingsManager.Current;

        if (SettingsManager.Current.Startup == true) // add to startup programs if enabled, needs improvement
        {
            RegistryKey? key = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
            string? executablePath = Environment.ProcessPath;
            if (key != null && executablePath != null)
                key.SetValue("PulseFlyout", executablePath);
        }

        // display tray icon if enabled
        if (!SettingsManager.Current.NIconHide)
        {
            nIcon.Visibility = Visibility.Visible;
        }

        cts = new CancellationTokenSource();

        mediaManager.Start();

        _hookProc = HookCallback;
        _hookId = SetHook(_hookProc);

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -Width - 20; // workaround for window appearing on the screen before the animation starts
        CustomWindowChrome.CaptionHeight = 0; // hide the title bar

        mediaManager.OnAnyMediaPropertyChanged += MediaManager_OnAnyMediaPropertyChanged;
        mediaManager.OnAnyPlaybackStateChanged += CurrentSession_OnPlaybackStateChanged;
        mediaManager.OnAnyTimelinePropertyChanged += MediaManager_OnAnyTimelinePropertyChanged;
        mediaManager.OnAnySessionClosed += MediaManager_OnAnySessionClosed;

        WM_TASKBARCREATED = RegisterWindowMessage("TaskbarCreated");
        WM_SHELLHOOK = RegisterWindowMessage("SHELLHOOK");
        RegisterShellHookWindow(new WindowInteropHelper(this).Handle);

        _positionTimer = new Timer(SeekbarUpdateUi, null, Timeout.Infinite, Timeout.Infinite);
        if (_seekBarEnabled && GetActiveMediaSession() is { } session)
        {
            UpdateSeekbarCurrentDuration(session.ControlSession.GetTimelineProperties().Position);
        }

        string previousVersion = SettingsManager.Current.LastKnownVersion;
        _ = CheckForExperimentsOnStartupAsync(previousVersion);

        // apply other things on new thread
        Dispatcher.Invoke(() =>
        {
            LocalizationManager.ApplyLocalization();

            try // update last known version. gets the version of the app, works only in release mode
            {
                var version = Package.Current.Id.Version;
                SettingsManager.Current.LastKnownVersion = $"v{version.Major}.{version.Minor}.{version.Build}";
            }
            catch
            {
                SettingsManager.Current.LastKnownVersion = "debug";
            }

            Logger.Info($"Current version: {SettingsManager.Current.LastKnownVersion}");

            Notifications.ShowFirstOrUpdateNotification(previousVersion, SettingsManager.Current.LastKnownVersion);
            FlowDirection = SettingsManager.Current.FlowDirection;

            // check for updates on startup
            _ = CheckForUpdatesOnStartupAsync();
        });
    }

    private async Task CheckForExperimentsOnStartupAsync(string previousVersion)
    {
        await ExperimentsService.GetExperimentsAsync();

        OnboardingExperiment(previousVersion);
    }

    private void OnboardingExperiment(string previousVersion)
    {
        // show onboarding to new users (no previous version stored = user has never run the app before)
        if (string.IsNullOrEmpty(previousVersion))
        {
            if (ExperimentsService.HasExperiments)
            {
                if (ExperimentsService.CheckUuidInExperiment("onboarding") == "A")
                    OnboardingWindow.ShowInstance();
                else
                {
                    SettingsWindow.ShowInstance();
                    _ = TelemetryService.SendTelemetryEventAsync("onboarding_completed", "onboarding");
                }
            }
            else
                OnboardingWindow.ShowInstance();
        }
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        try
        {
            var result = await UpdateCheckerService.CheckForUpdatesAsync(SettingsManager.Current.LastKnownVersion);

            if (result.Success)
            {
                UpdateState.Current.IsUpdateAvailable = result.IsUpdateAvailable;
                UpdateState.Current.NewestVersion = result.NewestVersion;
                UpdateState.Current.UpdateUrl = result.UpdateUrl;
                UpdateState.Current.LastUpdateCheck = result.CheckedAt;

                if (result.IsUpdateAvailable)
                {
                    Notifications.ShowUpdateAvailableNotification(result.NewestVersion, result.UpdateUrl);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to check for updates on startup");
        }
    }

    public bool IsSessionAllowed(MediaSession? session)
    {
        if (session == null) return false;
        if (!SettingsManager.Current.AppFilteringEnabled) return true;

        string appId = session.Id ?? string.Empty;
        string appName = MediaPlayerData.GetAndCacheMediaPlayerData(appId).Item1 ?? appId;

        if (SettingsManager.Current.AppFilteringMode == 0) // Blacklist mode
        {
            if (SettingsManager.Current.BlockedApps != null &&
                SettingsManager.Current.BlockedApps.Any(b => MatchesFilterEntry(b, appName, appId)))
                return false;

            return true;
        }
        else // Whitelist mode
        {
            if (SettingsManager.Current.AllowedApps != null &&
                SettingsManager.Current.AllowedApps.Any(a => MatchesFilterEntry(a, appName, appId)))
                return true;

            return false;
        }
    }

    // display names are matched in full: entries always hold a whole app name, and a substring match let one entry
    // swallow others (blocking "Amazon Music" also blocked "Amazon Music SMTC Bridge"). the session id keeps substring
    // matching so an entry can still target a whole package or publisher
    private static bool MatchesFilterEntry(string entry, string appName, string appId)
    {
        if (string.IsNullOrWhiteSpace(entry)) return false; // an empty entry would match every session

        return appName.Equals(entry, StringComparison.OrdinalIgnoreCase) || appId.Contains(entry, StringComparison.OrdinalIgnoreCase);
    }

    public MediaSession? GetActiveMediaSession()
    {
        var validSessions = mediaManager.CurrentMediaSessions.Values.Where(IsSessionAllowed).ToList();

        if (validSessions.Count == 0) return null;

        // A preferred app should only win while it has an active (playing) session.
        // Paused/closed sessions are kept as a final fallback, otherwise they can
        // mask another player that is currently active.
        var activeSessions = validSessions.Where(IsActiveMediaSession).ToList();

        var preferred = SettingsManager.Current.PreferredMediaApp;
        IEnumerable<string> preferredApps = SettingsManager.Current.PreferredMediaAppOrder ?? [];
        if (!preferredApps.Any() && !string.IsNullOrWhiteSpace(preferred))
            preferredApps = preferredApps.Prepend(preferred).ToList();
        if (preferredApps.Any())
        {
            foreach (var preferredApp in preferredApps)
            {
                var preferredSession = activeSessions.FirstOrDefault(session =>
                {
                    var appId = session.Id ?? string.Empty;
                    var appName = MediaPlayerData.GetAndCacheMediaPlayerData(appId).Item1 ?? appId;
                    return MatchesFilterEntry(preferredApp, appName, appId);
                });
                if (preferredSession != null) return preferredSession;
            }
        }

        var focused = mediaManager.GetFocusedSession();
        if (focused != null && activeSessions.Any(s => s.Id == focused.Id))
            return focused;

        return activeSessions.FirstOrDefault()
            ?? (focused != null && validSessions.Any(s => s.Id == focused.Id) ? focused : null)
            ?? validSessions.FirstOrDefault();
    }

    private static bool IsActiveMediaSession(MediaSession session)
    {
        try
        {
            var status = session.ControlSession?.GetPlaybackInfo()?.PlaybackStatus;
            return status is
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing or
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing;
        }
        catch
        {
            return false;
        }
    }

    public void RefreshFilteredMedia()
    {
        RefreshActiveMediaSelection();
    }

    private string? _lastSelectedMediaSessionId;

    private void RefreshActiveMediaSelection()
    {
        var activeSession = GetActiveMediaSession();
        var activeId = activeSession?.Id;
        var changed = !string.Equals(_lastSelectedMediaSessionId, activeId, StringComparison.Ordinal);
        _lastSelectedMediaSessionId = activeId;

        UpdateTaskbar();

        if (IsVisible && changed)
        {
            // UpdateUI handles a null value internally so we haven't checked for null here.
            UpdateUI(activeSession!);

            if (activeSession != null)
            {
                HandlePlayBackState(activeSession.ControlSession.GetPlaybackInfo()?.PlaybackStatus);
            }
            else
            {
                HandlePlayBackState(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed);
            }
        }
    }

    private static GlobalSystemMediaTransportControlsSessionMediaProperties? TryGetMediaProperties(GlobalSystemMediaTransportControlsSession controlSession)
    {
        try
        {
            return controlSession.TryGetMediaPropertiesAsync().GetAwaiter().GetResult();
        }
        catch (COMException ex)
        {
            Logger.Error(ex, "Failed to retrieve data from the player");
            return null;
        }
        catch (InvalidOperationException ex)
        {
            Logger.Debug(ex, "Media session became unavailable while reading properties");
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }

    private void openSettings(object? sender, EventArgs e)
    {
        SettingsWindow.ShowInstance();
    }

    public static int getDuration() // get the duration of the animation based on the speed setting
    {
        int msDuration = SettingsManager.Current.FlyoutAnimationSpeed switch
        {
            0 => 0, // off
            1 => 150, // 0.5x
            2 => 300, // 1x
            3 => 450, // 1.5x
            4 => 600, // 2x
            _ => 900 // 3x
        };
        return msDuration;
    }

    public EasingFunctionBase getEasingStyle(bool easeOut)
    {
        EasingMode easingMode = easeOut ? EasingMode.EaseOut : EasingMode.EaseIn;
        EasingFunctionBase easingStyle = SettingsManager.Current.FlyoutAnimationEasingStyle switch
        {
            // 0 is linear, null
            1 => new SineEase { EasingMode = easingMode }, // sine
            2 => new QuadraticEase { EasingMode = easingMode }, // quadratic
            _ => new CubicEase { EasingMode = easingMode }, // cubic
        };
        return easingStyle;
    }

    private MonitorUtil.MonitorInfo getSelectedMonitor()
    {
        return MonitorUtil.GetSelectedMonitor(SettingsManager.Current.FlyoutSelectedMonitor);
    }

    /// <summary>
    /// Computes the final resting position (left, top) for a window based on the current
    /// position setting and the selected monitor's work area.
    /// </summary>
    private (double left, double top) GetFinalPosition(Rect windowRect, Rect workArea)
    {
        int position = SettingsManager.Current.Position;
        double left = position switch
        {
            0 or 3 => workArea.Left + 16,
            2 or 5 => workArea.Left + workArea.Width - windowRect.Width - 16,
            _ => workArea.Left + workArea.Width / 2 - windowRect.Width / 2
        };
        double top = position switch
        {
            0 or 2 => workArea.Top + workArea.Height - windowRect.Height - 16,
            1 => workArea.Top + workArea.Height - windowRect.Height - 80,
            _ => workArea.Top + 16
        };
        return (left, top);
    }

    public void OpenAnimation(MicaWindow window, bool alwaysBottom = false, MonitorInfo? selectedMonitor = null, MicaWindow? aboveReference = null)
    {
        var eventTriggers = window.Triggers[0] as EventTrigger;
        var beginStoryboard = eventTriggers.Actions[0] as BeginStoryboard;
        var storyboard = beginStoryboard.Storyboard;

        DoubleAnimation moveAnimation = (DoubleAnimation)storyboard.Children[0];
        var monitor = selectedMonitor != null ? selectedMonitor.Value : getSelectedMonitor();
        var workArea = monitor.workArea;

        // prevent flickering
        WindowHelper.SetVisibility(window, false); // window.Visibility = Visibility.Hidden works with some delay

        // Update the DPI by moving the window to the target workArea, ignoring WPF scaling
        WindowHelper.SetPosition(window, workArea.Left, workArea.Top);
        var windowRect = WindowHelper.GetPlacement(window); // here we take the updated window size in raw coordinates.

        double window_left = 0;

        // If a reference window is provided and visible, position the window next to it
        if (aboveReference != null && aboveReference.IsVisible)
        {
            // Here we work with raw monitor coordinates, without taking DPI into account.
            double refWidth = aboveReference.Width * monitor.dpiX / 96.0;
            double refHeight = aboveReference.Height * monitor.dpiY / 96.0;
            var refRect = new Rect(0, 0, refWidth, refHeight);
            var (refLeft, refTop) = GetFinalPosition(refRect, workArea);

            window_left = refLeft + refWidth / 2 - windowRect.Width / 2;
            double aboveTop = refTop - windowRect.Height - 8;
            bool isTop = SettingsManager.Current.Position switch
            {
                3 or 4 or 5 => true,
                _ => false
            };

            // If the reference window is too close to the top edge, we place the flyout below it instead of above to prevent it from going off-screen.
            if (isTop)
                aboveTop = refTop + refHeight + 8;

            moveAnimation.To = aboveTop;
            if (SettingsManager.Current.FlyoutAnimationSpeed == 0)
                moveAnimation.From = moveAnimation.To;
            else
                moveAnimation.From = isTop ? aboveTop - 20 : aboveTop + 20;
        }
        // default behavior: position the flyout based on the user's settings
        else if (alwaysBottom == false)
        {
            _position = SettingsManager.Current.Position;
            if (_position == 0)
            {
                window_left = workArea.Left + 16;
                moveAnimation.To = workArea.Top + workArea.Height - windowRect.Height - 16;
                if (SettingsManager.Current.FlyoutAnimationSpeed == 0) // if off, don't animate (just appear at the bottom)
                    moveAnimation.From = moveAnimation.To;
                else
                    moveAnimation.From = workArea.Top + workArea.Height - windowRect.Height + 4; // appear from the bottom of the screen
            }
            else if (_position == 1)
            {
                window_left = workArea.Left + workArea.Width / 2 - windowRect.Width / 2;
                moveAnimation.To = workArea.Top + workArea.Height - windowRect.Height - 80;
                if (SettingsManager.Current.FlyoutAnimationSpeed == 0)
                    moveAnimation.From = moveAnimation.To;
                else
                    moveAnimation.From = workArea.Top + workArea.Height - windowRect.Height - 60;
            }
            else if (_position == 2)
            {
                window_left = workArea.Left + workArea.Width - windowRect.Width - 16;
                moveAnimation.To = workArea.Top + workArea.Height - windowRect.Height - 16;
                if (SettingsManager.Current.FlyoutAnimationSpeed == 0)
                    moveAnimation.From = moveAnimation.To;
                else
                    moveAnimation.From = workArea.Top + workArea.Height - windowRect.Height + 4;
            }
            else if (_position == 3)
            {
                window_left = workArea.Left + 16;
                moveAnimation.To = workArea.Top + 16;
                if (SettingsManager.Current.FlyoutAnimationSpeed == 0)
                    moveAnimation.From = moveAnimation.To;
                else
                    moveAnimation.From = workArea.Top + -4;
            }
            else if (_position == 4)
            {
                window_left = workArea.Left + workArea.Width / 2 - windowRect.Width / 2;
                moveAnimation.To = workArea.Top + 16;
                if (SettingsManager.Current.FlyoutAnimationSpeed == 0)
                    moveAnimation.From = moveAnimation.To;
                else
                    moveAnimation.From = workArea.Top + -4;
            }
            else if (_position == 5)
            {
                window_left = workArea.Left + workArea.Width - windowRect.Width - 16;
                moveAnimation.To = workArea.Top + 16;
                if (SettingsManager.Current.FlyoutAnimationSpeed == 0)
                    moveAnimation.From = moveAnimation.To;
                else
                    moveAnimation.From = workArea.Top + -4;
            }
        }
        // other cases (e.g. if alwaysBottom is true): position the flyout at the bottom center of the screen
        else
        {
            window_left = workArea.Left + workArea.Width / 2 - windowRect.Width / 2;
            moveAnimation.To = workArea.Top + workArea.Height - windowRect.Height - 16;
            if (SettingsManager.Current.FlyoutAnimationSpeed == 0)
                moveAnimation.From = moveAnimation.To;
            else
                moveAnimation.From = workArea.Top + workArea.Height - windowRect.Height + 4;
        }

        // Set the initial position in raw coordinates.
        WindowHelper.SetPosition(window, window_left, moveAnimation.From!.Value);

        // Next coordinates will be used to set Window.Top, which takes DPI into account,
        // so we need to convert the coordinates to DPI scale.
        moveAnimation.From *= 96.0 / monitor.dpiY;
        moveAnimation.To *= 96.0 / monitor.dpiY;

        int msDuration = getDuration();

        DoubleAnimation opacityAnimation = (DoubleAnimation)storyboard.Children[1];
        if (SettingsManager.Current.FlyoutAnimationSpeed != 0) opacityAnimation.From = 0;
        opacityAnimation.To = 1;
        opacityAnimation.Duration = new Duration(TimeSpan.FromMilliseconds(msDuration));

        if (SettingsManager.Current.FlyoutAnimationEasingStyle == 0) moveAnimation.EasingFunction = opacityAnimation.EasingFunction = null;
        else moveAnimation.EasingFunction = opacityAnimation.EasingFunction = getEasingStyle(true);
        moveAnimation.Duration = new Duration(TimeSpan.FromMilliseconds(msDuration));

        storyboard.Begin(window);
        WindowHelper.SetVisibility(window, true);
        WindowHelper.SetTopmost(window);
    }

    public void CloseAnimation(MicaWindow window, MonitorInfo? selectedMonitor = null)
    {
        var eventTriggers = window.Triggers[0] as EventTrigger;
        var beginStoryboard = eventTriggers.Actions[0] as BeginStoryboard;
        var storyboard = beginStoryboard.Storyboard;

        DoubleAnimation moveAnimation = (DoubleAnimation)storyboard.Children[0];
        var monitor = selectedMonitor != null ? selectedMonitor.Value : getSelectedMonitor();
        var workArea = monitor.workArea;
        Rect windowRect = WindowHelper.GetPlacement(window);

        // Use the window's actual current position as the animation start
        moveAnimation.From = windowRect.Top;

        if (SettingsManager.Current.FlyoutAnimationSpeed != 0)
        {
            // Determine slide direction
            bool isTopHalf = windowRect.Top + windowRect.Height / 2 < workArea.Top + workArea.Height / 2;
            moveAnimation.To = windowRect.Top + (isTopHalf ? -20 : 20);
        }

        moveAnimation.From *= 96.0 / monitor.dpiY;
        if (moveAnimation.To != null)
            moveAnimation.To *= 96.0 / monitor.dpiY;

        int msDuration = getDuration();

        DoubleAnimation opacityAnimation = (DoubleAnimation)storyboard.Children[1];
        opacityAnimation.From = 1;
        if (SettingsManager.Current.FlyoutAnimationSpeed != 0) opacityAnimation.To = 0;
        opacityAnimation.Duration = new Duration(TimeSpan.FromMilliseconds(msDuration));

        if (SettingsManager.Current.FlyoutAnimationEasingStyle == 0) moveAnimation.EasingFunction = opacityAnimation.EasingFunction = null;
        else moveAnimation.EasingFunction = opacityAnimation.EasingFunction = getEasingStyle(false);
        moveAnimation.Duration = new Duration(TimeSpan.FromMilliseconds(msDuration));

        storyboard.Begin(window);
    }

    public void UpdateTaskbar()
    {
        var activeSession = GetActiveMediaSession();
        if (!mediaManager.IsStarted || activeSession == null)
        {
            taskbarWindow?.UpdateUi("-", "-", null, GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed);
            return;
        }

        var songInfo = TryGetMediaProperties(activeSession.ControlSession);
        if (songInfo == null)
            return;

        _ = PreloadLyricsAsync(activeSession, songInfo.Title, songInfo.Artist);

        var playbackInfo = activeSession.ControlSession.GetPlaybackInfo();
        var thumbnail = BitmapHelper.GetThumbnail(songInfo.Thumbnail);
        UpdateAccentColorsIfNeeded(songInfo.Title, songInfo.Artist);
        taskbarWindow?.UpdateUi(songInfo.Title, songInfo.Artist, thumbnail, playbackInfo.PlaybackStatus, playbackInfo.Controls);
    }

    public void reportBug(object? sender, EventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://github.com/LoongSerpent9Realms/FluentFlyout/tree/master/FluentFlyoutMSIX",
            UseShellExecute = true
        });
    }

    private void openRepository(object? sender, EventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://github.com/LoongSerpent9Realms/FluentFlyout/tree/master/FluentFlyoutMSIX",
            UseShellExecute = true
        });
    }

    public void openLogsFolder(object? sender, EventArgs e)
    {
        try
        {
            Process.Start("explorer.exe", FileSystemHelper.GetLogsPath());
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to open logs folder");
        }
    }

    private void pauseOtherMediaSessionsIfNeeded(MediaSession mediaSession)
    {
        if (
            SettingsManager.Current.PauseOtherSessionsEnabled
            && mediaSession.ControlSession.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
            )
        {
            PauseOtherSessions(mediaSession);
        }
    }

    private void CurrentSession_OnPlaybackStateChanged(MediaSession mediaSession, GlobalSystemMediaTransportControlsSessionPlaybackInfo? playbackInfo = null)
    {
        // WindowsMediaController raises callbacks from its worker threads. Keep all
        // WPF state and window access on the owning dispatcher thread.
        if (!Dispatcher.CheckAccess())
        {
            if (_isCleaningUp || Dispatcher.HasShutdownStarted) return;
            _ = Dispatcher.BeginInvoke(() => CurrentSession_OnPlaybackStateChanged(mediaSession, playbackInfo));
            return;
        }

#if DEBUG
        Logger.Debug("Playback state changed: " + mediaSession.Id + " " + mediaSession.ControlSession.GetPlaybackInfo().PlaybackStatus);
#endif     
        pauseOtherMediaSessionsIfNeeded(mediaSession);

        var focusedSession = GetActiveMediaSession();
        if (focusedSession == null)
        {
            taskbarWindow?.UpdateUi("-", "-", null, GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed);
            return;
        }

        var tbSongInfo = TryGetMediaProperties(focusedSession.ControlSession);
        if (tbSongInfo != null)
        {
            var tbThumbnail = BitmapHelper.GetThumbnail(tbSongInfo.Thumbnail);
            UpdateAccentColorsIfNeeded(tbSongInfo.Title, tbSongInfo.Artist);
            var tbPlayback = focusedSession.ControlSession.GetPlaybackInfo();

            taskbarWindow?.UpdateUi(tbSongInfo.Title, tbSongInfo.Artist, tbThumbnail, tbPlayback?.PlaybackStatus, tbPlayback?.Controls);
        }

        if (IsVisible)
        {
            UpdateUI(focusedSession);
            HandlePlayBackState(playbackInfo?.PlaybackStatus);
        }
    }

    // for determining whether MediaPropertyChanged has no changes
    private string previousMediaProperty = "";
    private int previousMediaPropertyThumbnail = 0;
    private void MediaManager_OnAnyMediaPropertyChanged(MediaSession mediaSession, GlobalSystemMediaTransportControlsSessionMediaProperties mediaProperties)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (_isCleaningUp || Dispatcher.HasShutdownStarted) return;
            _ = Dispatcher.BeginInvoke(() => MediaManager_OnAnyMediaPropertyChanged(mediaSession, mediaProperties));
            return;
        }

        // sometimes mediaSession.ControlSession can be null
        if (mediaSession.ControlSession == null)
            return;

#if DEBUG
        Logger.Debug("Media property changed: " + mediaProperties.Title + " " + mediaSession.ControlSession.GetPlaybackInfo().PlaybackStatus);
#endif
        var currentActiveSession = GetActiveMediaSession();
        if (currentActiveSession == null)
        {
            SetNeteaseLikeVisibility(false);
            taskbarWindow?.UpdateUi("-", "-", null, GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed);
            return;
        }

        var songInfo = TryGetMediaProperties(currentActiveSession.ControlSession);
        if (songInfo == null)
            return;

        SetNeteaseLikeVisibility(IsNeteaseSession(currentActiveSession));

        _ = PluginManager.Current.PublishMediaEventAsync(new PluginMediaEvent("media-properties-changed", mediaSession.Id.ToString(), songInfo.Title, songInfo.Artist));

        _ = PreloadLyricsAsync(currentActiveSession, songInfo.Title, songInfo.Artist);

        var playbackInfo = currentActiveSession.ControlSession.GetPlaybackInfo();

        string check = songInfo.Title + songInfo.Artist + playbackInfo.PlaybackStatus;
        int checkThumbnail = BitmapHelper.GetStableThumbnailHash(songInfo.Thumbnail);
        bool onlyThumbnailChanged = false;
        if (previousMediaProperty == check)
        {
            onlyThumbnailChanged = true;
            if (previousMediaPropertyThumbnail == checkThumbnail)
                return; // prevent multiple calls for the same song info
        }

        previousMediaProperty = check;
        previousMediaPropertyThumbnail = checkThumbnail;

        _neteaseLikeSongKey = BuildNeteaseSongKey(songInfo.Title, songInfo.Artist);
        _neteaseLikeStateCts?.Cancel();
        _neteaseLikeStateCts?.Dispose();
        _neteaseLikeStateCts = new CancellationTokenSource();
        UpdateNeteaseLikeVisual(false);
        _ = UpdateNeteaseLikeStateAsync(songInfo.Title, songInfo.Artist, _neteaseLikeSongKey, _neteaseLikeStateCts.Token);

        var thumbnail = BitmapHelper.GetThumbnail(songInfo.Thumbnail);
        UpdateAccentColorsIfNeeded(songInfo.Title, songInfo.Artist);

        taskbarWindow?.UpdateUi(songInfo.Title, songInfo.Artist, thumbnail, playbackInfo.PlaybackStatus, playbackInfo.Controls);

        pauseOtherMediaSessionsIfNeeded(mediaSession);

        if (SettingsManager.Current.NextUpEnabled && !FullscreenDetector.IsFullscreenApplicationRunning()) // show NextUpWindow if enabled in settings
        {
            void createNewNextUpWindow()
            {
                Dispatcher.Invoke(() =>
                {
                    if (nextUpWindow == null && playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) // double-check within the Dispatcher to prevent race conditions
                    {
                        nextUpWindow = new NextUpWindow(songInfo.Title, songInfo.Artist, thumbnail);
                        currentTitle = songInfo.Title;
                        nextUpWindow.Closed += (s, e) => nextUpWindow = null; // set nextUpWindow to null when closed
                    }
                });
            }

            if (nextUpWindow == null && IsVisible == false && songInfo.Thumbnail != null && currentTitle != songInfo.Title)
            {
                createNewNextUpWindow();
            }
            else if (nextUpWindow != null && !onlyThumbnailChanged)
            {
                Dispatcher.Invoke(() =>
                {
                    if (nextUpWindow != null)
                    {
                        WindowHelper.SetVisibility(nextUpWindow, false); // prevents rare flickering during rapid closing
                        nextUpWindow.Close(); // must be cleared by the Closed event
                    }
                });
                createNewNextUpWindow();
            }
            else if (nextUpWindow != null && songInfo.Thumbnail != null)
            {
                Dispatcher.Invoke(() =>
                {
                    nextUpWindow?.UpdateThumbnail(thumbnail);
                });
            }
        }

        if (IsVisible)
        {
            var focusedSession = GetActiveMediaSession();
            if (focusedSession != null)
            {
                HandlePlayBackState(focusedSession.ControlSession.GetPlaybackInfo()?.PlaybackStatus);
                UpdateUI(focusedSession);
            }
        }
    }

    private void MediaManager_OnAnyTimelinePropertyChanged(MediaSession mediaSession, GlobalSystemMediaTransportControlsSessionTimelineProperties timelineProperties)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (_isCleaningUp || Dispatcher.HasShutdownStarted) return;
            _ = Dispatcher.BeginInvoke(() => MediaManager_OnAnyTimelinePropertyChanged(mediaSession, timelineProperties));
            return;
        }

        _lastSelfUpdateTimestamp = DateTime.Now;
        _ = PluginManager.Current.PublishMediaEventAsync(new PluginMediaEvent("timeline-changed", mediaSession.Id.ToString()));

        if (GetActiveMediaSession() is not { } session || session.Id != mediaSession.Id) return;

        if (_seekBarEnabled)
        {
            Dispatcher.Invoke(() =>
            {
                if (!IsActive || _isDragging) return;

                UpdateSeekbarCurrentDuration(session.ControlSession.GetTimelineProperties().Position);
                HandlePlayBackState(session.ControlSession.GetPlaybackInfo().PlaybackStatus);
            });
        }
    }

    private void MediaManager_OnAnySessionClosed(MediaSession mediaSession)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (_isCleaningUp || Dispatcher.HasShutdownStarted) return;
            _ = Dispatcher.BeginInvoke(() => MediaManager_OnAnySessionClosed(mediaSession));
            return;
        }

#if DEBUG
        Logger.Debug("Session closed: " + (mediaSession.Id).ToString());
#endif
        UpdateTaskbar();
        _ = PluginManager.Current.PublishMediaEventAsync(new PluginMediaEvent("session-closed", mediaSession.Id.ToString()));
    }

    private void BuildPluginTrayItems()
    {
        foreach (var pluginItem in PluginManager.Current.TrayItems)
        {
            var menuItem = new Wpf.Ui.Controls.MenuItem { Header = pluginItem.Header, ToolTip = pluginItem.ToolTip };
            menuItem.Click += async (_, _) =>
            {
                try { await pluginItem.Activate(); }
                catch (Exception ex) { Logger.Error(ex, "Plugin tray action failed: {0}", pluginItem.Id); }
            };
            PluginTrayMenu.Items.Insert(Math.Max(0, PluginTrayMenu.Items.Count - 1), menuItem);
        }
    }

    private static IntPtr SetHook(LowLevelKeyboardProc proc) // set the keyboard hook
    {
        using Process curProcess = Process.GetCurrentProcess();
        using ProcessModule? curModule = curProcess.MainModule;
        if (curModule == null)
        {
            Logger.Warn("Failed to set keyboard hook - PulseFlyout will now rely on WndProc only");
            return IntPtr.Zero;
        }
        return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == WM_KEYDOWN || wParam == WM_KEYUP))
        {
            int vkCode = Marshal.ReadInt32(lParam);

            bool mediaKeysPressed = vkCode == 0xB3 || vkCode == 0xB0 || vkCode == 0xB1 || vkCode == 0xB2; // Play/Pause, next, previous, stop
            bool volumeKeysPressed = vkCode == 0xAD || vkCode == 0xAE || vkCode == 0xAF; // Mute, Volume Down, Volume Up

            // MainWindow.WndProc() also handles media and volume keys
            if (mediaKeysPressed || volumeKeysPressed)
            {
                bool result = false;
                if (mediaKeysPressed || (!SettingsManager.Current.MediaFlyoutVolumeKeysExcluded && volumeKeysPressed))
                    result = TryShowMediaFlyoutDebounced();

                if (SettingsManager.Current.VolumeControlEnabled)
                {
                    volumeMixerWindow?.ViewModel.SyncMasterFromDevice();
                    volumeMixerWindow?.ShowFlyout();
                }

                if (!result)
                {
                    return CallNextHookEx(_hookId, nCode, wParam, lParam);
                }
            }

            if (SettingsManager.Current.LockKeysEnabled
                && !FullscreenDetector.IsFullscreenApplicationRunning()
                && wParam == WM_KEYUP)
            {
                if (vkCode == 0x14 && SettingsManager.Current.LockKeysCapsEnabled) // Caps Lock
                {
                    lockWindow ??= new LockWindow();
                    lockWindow.ShowLockFlyout(FindResource("LockWindow_CapsLock").ToString(), Keyboard.IsKeyToggled(Key.CapsLock));
                }
                else if (vkCode == 0x90 && SettingsManager.Current.LockKeysNumEnabled) // Num Lock
                {
                    lockWindow ??= new LockWindow();
                    lockWindow.ShowLockFlyout(FindResource("LockWindow_NumLock").ToString(), Keyboard.IsKeyToggled(Key.NumLock));
                }
                else if (vkCode == 0x91 && SettingsManager.Current.LockKeysScrollEnabled) // Scroll Lock
                {
                    lockWindow ??= new LockWindow();
                    lockWindow.ShowLockFlyout(FindResource("LockWindow_ScrollLock").ToString(), Keyboard.IsKeyToggled(Key.Scroll));
                }
                else if (vkCode == 0x2D && SettingsManager.Current.LockKeysInsertEnabled) // Insert
                {
                    lockWindow ??= new LockWindow();
                    lockWindow.ShowLockFlyout("Insert", Keyboard.IsKeyToggled(Key.Insert));
                }
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    // show the media flyout with debounce
    private bool TryShowMediaFlyoutDebounced()
    {
        long currentTime = Environment.TickCount64;
        // debounce to prevent hangs with rapid key presses
        if ((currentTime - _lastFlyoutTime) < 500) // 500ms debounce time
        {
            return false;
        }
        _lastFlyoutTime = currentTime;
        ShowMediaFlyout();
        return true;
    }

    public async void ShowMediaFlyout(bool toggleMode = false, bool forceShow = false)
    {
        var activeSession = GetActiveMediaSession();
        if (activeSession == null ||
            (!forceShow && !SettingsManager.Current.MediaFlyoutEnabled) ||
            FullscreenDetector.IsFullscreenApplicationRunning())
            return;

        // If in toggle mode and flyout is visible, close it
        if (toggleMode && Visibility == Visibility.Visible && !_isHiding)
        {
            CloseAnimation(this);
            _isHiding = true;
            cts.Cancel();
            await Task.Delay(getDuration());
            if (_isHiding)
            {
                Hide();
                if (_seekBarEnabled)
                    HandlePlayBackState(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused);
            }
            return;
        }

        UpdateUI(activeSession);
        if (_seekBarEnabled)
            HandlePlayBackState(activeSession.ControlSession.GetPlaybackInfo().PlaybackStatus);

        if (nextUpWindow != null) // close NextUpWindow if it's open
        {
            nextUpWindow.Close();
            nextUpWindow = null;
        }

        if (_isHiding == true)
        {
            _isHiding = false;
            OpenAnimation(this);
        }
        cts.Cancel();
        cts = new CancellationTokenSource();
        var token = cts.Token;
        Visibility = Visibility.Visible;
        WindowHelper.SetTopmost(this);

        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(100, token); // check if mouse is over every 100ms

                bool mouseOverMedia = WindowHelper.IsMouseOverWindow(this);
                bool mouseOverVolume = SettingsManager.Current.VolumeControlAboveMediaFlyout
                    && SettingsManager.Current.VolumeControlEnabled
                    && volumeMixerWindow != null
                    && volumeMixerWindow.IsVisible
                    && WindowHelper.IsMouseOverWindow(volumeMixerWindow); // sync with VolumeMixerWindow

                if (!mouseOverMedia && !mouseOverVolume && !SettingsManager.Current.MediaFlyoutAlwaysDisplay)
                {
                    await Task.Delay(SettingsManager.Current.Duration, token);

                    mouseOverMedia = WindowHelper.IsMouseOverWindow(this);
                    mouseOverVolume = SettingsManager.Current.VolumeControlAboveMediaFlyout
                        && SettingsManager.Current.VolumeControlEnabled
                        && volumeMixerWindow != null
                        && volumeMixerWindow.IsVisible
                        && WindowHelper.IsMouseOverWindow(volumeMixerWindow);

                    if (!mouseOverMedia && !mouseOverVolume)
                    {
                        CloseAnimation(this);
                        _isHiding = true;
                        await Task.Delay(getDuration());
                        if (_isHiding == false) return;
                        Hide();
                        if (_seekBarEnabled)
                            HandlePlayBackState(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused);
                        break;
                    }
                }
            }
        }
        catch (TaskCanceledException)
        {
            // task was canceled, do nothing
        }
    }

    private void UpdateMediaFlyoutCloseButtonVisibility()
    {
        MediaFlyoutCloseButton.Visibility = SettingsManager.Current.MediaFlyoutAlwaysDisplay && !SettingsManager.Current.CompactLayout ? Visibility.Visible : Visibility.Collapsed;
        ControlClose.Visibility = SettingsManager.Current.MediaFlyoutAlwaysDisplay && SettingsManager.Current.CompactLayout ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateUI(MediaSession mediaSession)
    {
        if (_layout != SettingsManager.Current.CompactLayout ||
            _shuffleEnabled != SettingsManager.Current.ShuffleEnabled ||
            _repeatEnabled != SettingsManager.Current.RepeatEnabled ||
            _playerInfoEnabled != SettingsManager.Current.PlayerInfoEnabled ||
            _centerTitleArtist != SettingsManager.Current.CenterTitleArtist ||
            _seekBarEnabled != SettingsManager.Current.SeekbarEnabled ||
            _alwaysDisplay != SettingsManager.Current.MediaFlyoutAlwaysDisplay)
            UpdateUILayout();

        // sometimes mediaSession.ControlSession can be null
        if (mediaSession.ControlSession == null)
            return;

        var controlSession = mediaSession.ControlSession;

        Dispatcher.Invoke(() =>
        {
            UpdateMediaFlyoutCloseButtonVisibility();
            this.EnableBackdrop(); // ensures the backdrop is enabled as sometimes it gets disabled

            if (mediaSession == null)
            {
                SongTitle.Text = "No media playing";
                SongArtist.Text = string.Empty;
                SongImage.ImageSource = null;
                SymbolPlayPause.Symbol = Wpf.Ui.Controls.SymbolRegular.Stop16;
                ControlPlayPause.IsEnabled = false;
                ControlPlayPause.Opacity = 0.35;
                ControlBack.IsEnabled = ControlForward.IsEnabled = false;
                ControlBack.Opacity = ControlForward.Opacity = 0.35;
                SongInfoStackPanel.ToolTip = string.Empty;
                return;
            }

            var mediaProperties = controlSession.GetPlaybackInfo();
            if (mediaProperties != null)
            {
                if (mediaProperties.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    SymbolPlayPause.Symbol = Wpf.Ui.Controls.SymbolRegular.Pause16;
                }
                else
                {
                    SymbolPlayPause.Symbol = Wpf.Ui.Controls.SymbolRegular.Play16;
                }

                ControlPlayPause.IsEnabled = mediaProperties.Controls.IsPlayEnabled || mediaProperties.Controls.IsPauseEnabled;

                if (ControlPlayPause.IsEnabled)
                {
                    ControlPlayPause.Opacity = 1;
                }
                else
                {
                    ControlPlayPause.Opacity = 0.35;
                }

                ControlBack.IsEnabled = ControlForward.IsEnabled = mediaProperties.Controls.IsNextEnabled;
                ControlBack.Opacity = ControlForward.Opacity = mediaProperties.Controls.IsNextEnabled ? 1 : 0.35;

                if (SettingsManager.Current.RepeatEnabled && !SettingsManager.Current.CompactLayout)
                {
                    ControlRepeat.Visibility = Visibility.Visible;
                    ControlRepeat.IsEnabled = mediaProperties.Controls.IsRepeatEnabled;
                    ControlRepeat.Opacity = mediaProperties.Controls.IsRepeatEnabled ? 1 : 0.35;
                    if (mediaProperties.AutoRepeatMode == global::Windows.Media.MediaPlaybackAutoRepeatMode.List)
                    {
                        SymbolRepeat.Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowRepeatAll24;
                        SymbolRepeat.Opacity = 1;
                    }
                    else if (mediaProperties.AutoRepeatMode == global::Windows.Media.MediaPlaybackAutoRepeatMode.Track)
                    {
                        SymbolRepeat.Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowRepeat124;
                        SymbolRepeat.Opacity = 1;
                    }
                    else if (mediaProperties.AutoRepeatMode == global::Windows.Media.MediaPlaybackAutoRepeatMode.None)
                    {
                        SymbolRepeat.Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowRepeatAllOff24;
                        SymbolRepeat.Opacity = 0.5;
                    }
                }
                else ControlRepeat.Visibility = Visibility.Collapsed;


                if (SettingsManager.Current.ShuffleEnabled && !SettingsManager.Current.CompactLayout)
                {
                    ControlShuffle.Visibility = Visibility.Visible;
                    ControlShuffle.IsEnabled = mediaProperties.Controls.IsShuffleEnabled;
                    ControlShuffle.Opacity = mediaProperties.Controls.IsShuffleEnabled ? 1 : 0.35;
                    if (mediaProperties.IsShuffleActive == true)
                    {
                        SymbolShuffle.Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowShuffle24;
                        SymbolShuffle.Opacity = 1;
                    }
                    else
                    {
                        SymbolShuffle.Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowShuffleOff24;
                        SymbolShuffle.Opacity = 0.5;
                    }
                }
                else ControlShuffle.Visibility = Visibility.Collapsed;


                if (SettingsManager.Current.PlayerInfoEnabled && !SettingsManager.Current.CompactLayout)
                {
                    MediaIdButton.Visibility = Visibility.Visible;
                    (string title, ImageSource? Icon) = MediaPlayerData.GetAndCacheMediaPlayerData(mediaSession.Id);
                    MediaId.Text = title;
                    if (Icon != null)
                    {
                        MediaIdIcon.Source = Icon;
                        MediaIdIcon.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        MediaIdIcon.Visibility = Visibility.Collapsed;
                    }
                }
                else MediaIdButton.Visibility = Visibility.Collapsed;

                // background blurred image visibility setting
                BackgroundImageStyle1.Visibility = SettingsManager.Current.MediaFlyoutBackgroundBlur == 1 ? Visibility.Visible : Visibility.Collapsed;
                BackgroundImageStyle2.Visibility = SettingsManager.Current.MediaFlyoutBackgroundBlur == 2 ? Visibility.Visible : Visibility.Collapsed;
                BackgroundImageStyle3.Visibility = SettingsManager.Current.MediaFlyoutBackgroundBlur == 3 ? Visibility.Visible : Visibility.Collapsed;

                // color play/pause button
                if (BitmapHelper.SavedDominantColors.Count > 0)
                {
                    SolidColorBrush brush = BitmapHelper.SavedDominantColors.First();
                    ControlPlayPause.Background = brush;
                }

                // acrylic effect setting
                if (SettingsManager.Current.MediaFlyoutAcrylicWindowEnabled != _acrylicEnabled
                || SettingsManager.Current.AppTheme != _themeOption) // if theme changes, reapply acrylic for updated background color
                {
                    _acrylicEnabled = SettingsManager.Current.MediaFlyoutAcrylicWindowEnabled;
                    ToggleBlur(); // called enabled but it actually toggles based on the setting
                }
            }

            var songInfo = TryGetMediaProperties(controlSession);
            if (songInfo == null)
                return;

            if (songInfo != null)
            {
                SongTitle.Text = songInfo.Title;
                SongArtist.Text = songInfo.Artist;
                var image = BitmapHelper.GetThumbnail(songInfo.Thumbnail);
                SongImage.ImageSource = image;

                // set tooltip
                SongInfoStackPanel.ToolTip = string.Empty;
                SongInfoStackPanel.ToolTip += !String.IsNullOrEmpty(songInfo.Title) ? songInfo.Title : string.Empty;
                SongInfoStackPanel.ToolTip += !String.IsNullOrEmpty(songInfo.Artist) ? "\n\n" + songInfo.Artist : string.Empty;

                // background blurred image
                if (SettingsManager.Current.MediaFlyoutBackgroundBlur != 0)
                {
                    // make image 1:1 aspect ratio so gradient masks work for non-square images
                    var croppedImage = BitmapHelper.CropToSquare(image);

                    switch (SettingsManager.Current.MediaFlyoutBackgroundBlur)
                    {
                        case 1:
                            BackgroundImageStyle1.Source = croppedImage;
                            break;
                        case 2:
                            BackgroundImageStyle2.Source = croppedImage;
                            break;
                        case 3:
                            BackgroundImageStyle3.Source = croppedImage;
                            break;
                    }
                }

                if (SongImage.ImageSource == null) SongImagePlaceholder.Visibility = Visibility.Visible;
                else SongImagePlaceholder.Visibility = Visibility.Collapsed;

                if (_seekBarEnabled)
                {
                    var timeline = controlSession.GetTimelineProperties();

                    // State tracking
                    bool mediaSessionSupportsSeekbar = timeline.MaxSeekTime.TotalSeconds >= 1.0; // Heuristics

                    if (_mediaSessionSupportsSeekbar != mediaSessionSupportsSeekbar)
                    {
                        _mediaSessionSupportsSeekbar = mediaSessionSupportsSeekbar;
                        UpdateUILayout();
                        // Force refly
                        _isHiding = true;
                        ShowMediaFlyout();
                    }

                    if (mediaSessionSupportsSeekbar)
                    {
                        Seekbar.Maximum = timeline.MaxSeekTime.TotalSeconds;
                        SeekbarMaxDuration.Text = timeline.MaxSeekTime.ToString(timeline.MaxSeekTime.Hours > 0 ? @"hh\:mm\:ss" : @"mm\:ss");
                    }
                }
            }
        });
    }

    private void UpdateUILayout() // update the layout based on the settings
    {
        Dispatcher.Invoke(() =>
        {
            int extraWidth = SettingsManager.Current.RepeatEnabled ? 36 : 0;
            extraWidth += SettingsManager.Current.ShuffleEnabled ? 36 : 0;
            extraWidth += 36; // lyrics button
            extraWidth += NeteaseLikeButton.Visibility == Visibility.Visible ? 36 : 0;
            extraWidth += SettingsManager.Current.PlayerInfoEnabled ? 72 : 0;
            // keep minimum width at 72 even if all extra features are disabled to prevent the widget from being too small
            extraWidth = Math.Max(extraWidth, 72);

            int extraHeight = SettingsManager.Current.SeekbarEnabled && _mediaSessionSupportsSeekbar ? 36 : 0;

            if (SettingsManager.Current.CompactLayout) // compact layout
            {
                Height = 60 + extraHeight;
                Width = 400;
                BodyStackPanel.Orientation = Orientation.Horizontal;
                BodyStackPanel.Width = 300;
                ControlsStackPanel.Margin = new Thickness(0);
                ControlsStackPanel.Width = 140;
                MediaIdButton.Visibility = Visibility.Collapsed;
                SongImageBorder.Margin = new Thickness(0);
                SongImageBorder.Height = 36;
                SongInfoStackPanel.Margin = new Thickness(8, 0, 0, 0);
                SongInfoStackPanel.Width = 182;
                if (SettingsManager.Current.MediaFlyoutAlwaysDisplay)
                {
                    SongInfoStackPanel.Width -= 36;
                    ControlsStackPanel.Width += 44;
                }
            }
            else // normal layout
            {
                Height = 112 + extraHeight;
                Width = 310 - 72 + extraWidth;
                BodyStackPanel.Orientation = Orientation.Vertical;
                BodyStackPanel.Width = 194 - 72 + extraWidth;
                ControlsStackPanel.Margin = Margin = new Thickness(12, 8, 0, 0);
                ControlsStackPanel.Width = 184 - 72 + extraWidth;
                MediaIdButton.Visibility = Visibility.Visible;
                SongImageBorder.Margin = new Thickness(6);
                SongImageBorder.Height = 78;
                SongInfoStackPanel.Margin = new Thickness(12, 0, 0, 0);
                SongInfoStackPanel.Width = 182 - 72 + extraWidth;
            }

            if (SettingsManager.Current.CenterTitleArtist)
            {
                SongTitle.HorizontalAlignment = HorizontalAlignment.Center;
                SongArtist.HorizontalAlignment = HorizontalAlignment.Center;
            }
            else
            {
                SongTitle.HorizontalAlignment = HorizontalAlignment.Left;
                SongArtist.HorizontalAlignment = HorizontalAlignment.Left;
            }

            if (SettingsManager.Current.SeekbarEnabled)
                SeekbarWrapper.Visibility = Visibility.Visible;
            else
                SeekbarWrapper.Visibility = Visibility.Collapsed;
        });

        _layout = SettingsManager.Current.CompactLayout;
        _repeatEnabled = SettingsManager.Current.RepeatEnabled;
        _shuffleEnabled = SettingsManager.Current.ShuffleEnabled;
        _playerInfoEnabled = SettingsManager.Current.PlayerInfoEnabled;
        _centerTitleArtist = SettingsManager.Current.CenterTitleArtist;
        _seekBarEnabled = SettingsManager.Current.SeekbarEnabled;
        _alwaysDisplay = SettingsManager.Current.MediaFlyoutAlwaysDisplay;
    }

    private async void MediaIdButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SettingsManager.Current.PlayerInfoEnabled || SettingsManager.Current.CompactLayout) return;
        e.Handled = true;
        if (GetActiveMediaSession() is { } activeSession)
        {
            var mediaProperties = TryGetMediaProperties(activeSession.ControlSession);
            await Task.Run(() => MediaPlayerData.TryActivateMediaPlayer(activeSession.Id, mediaProperties?.Title));
        }
    }

    private async void LyricsButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowLyricsForActiveSessionAsync();
    }

    private async void NeteaseLikeButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await ToggleNeteaseLikeAsync();
    }

    public async Task ToggleNeteaseLikeAsync()
    {
        Logger.Debug("Netease like button clicked");
        RecordNeteaseLikeEvent("红心按钮已点击");
        if (GetActiveMediaSession() is not { } session)
        {
            NeteaseLikeButton.ToolTip = "当前没有可用的媒体会话";
            RecordNeteaseLikeEvent("忽略：没有可用的媒体会话");
            Logger.Debug("Netease like ignored: no active media session");
            return;
        }
        var properties = TryGetMediaProperties(session.ControlSession);
        if (properties is null || string.IsNullOrWhiteSpace(properties.Title))
        {
            NeteaseLikeButton.ToolTip = "无法读取当前歌曲信息";
            RecordNeteaseLikeEvent("忽略：无法读取歌曲信息");
            Logger.Debug("Netease like ignored: media properties unavailable");
            return;
        }

        Logger.Debug("Netease like requested: '{0}' / '{1}'", properties.Title, properties.Artist);

        NeteaseLikeButton.IsEnabled = false;
        NeteaseLikeButton.ToolTip = "正在处理喜欢操作...";
        try
        {
            if (!NeteaseMusicService.IsLoggedIn && !await NeteaseLoginWindow.ShowLoginAsync(this))
            {
                NeteaseLikeButton.ToolTip = "需要先登录网易云音乐";
                RecordNeteaseLikeEvent("取消：网易云未登录或登录窗口未完成");
                Logger.Debug("Netease like cancelled: login was not completed");
                return;
            }

            var songKey = BuildNeteaseSongKey(properties.Title, properties.Artist);
            var songId = await NeteaseMusicService.ResolveSongIdAsync(properties.Title, properties.Artist);
            songId ??= LyricsService.TryGetCurrentLocalSongId(properties.Title);
            songId ??= LyricsService.TryGetLastSongId(properties.Title, properties.Artist);
            if (!string.Equals(_neteaseLikeSongKey, songKey, StringComparison.Ordinal))
            {
                NeteaseLikeButton.ToolTip = "歌曲已切换，请重试";
                return;
            }
            if (songId is not > 0)
            {
                NeteaseLikeButton.ToolTip = "当前媒体会话没有歌曲 ID";
                RecordNeteaseLikeEvent($"失败：当前会话没有有效歌曲 ID（session={session.Id}）");
                return;
            }

            RecordNeteaseLikeEvent($"请求：/like?id={songId}&like=true/false");
            var resolvedSongId = songId.Value;
            var wasLiked = await NeteaseMusicService.IsSongLikedAsync(resolvedSongId);
            if (!string.Equals(_neteaseLikeSongKey, songKey, StringComparison.Ordinal))
            {
                NeteaseLikeButton.ToolTip = "歌曲已切换，请重试";
                return;
            }
            var success = await NeteaseMusicService.LikeAsync(resolvedSongId, like: !wasLiked);
            Logger.Debug("Netease like result: songId={0}, action={1}, success={2}", songId, wasLiked ? "unlike" : "like", success);
            if (success && string.Equals(_neteaseLikeSongKey, songKey, StringComparison.Ordinal))
            {
                UpdateNeteaseLikeVisual(!wasLiked);
                RecordNeteaseLikeEvent($"成功：{(wasLiked ? "取消喜欢" : "喜欢")}，请求参数 id={songId}&like={!wasLiked}");
            }
            else
            {
                NeteaseLikeButton.ToolTip = wasLiked ? "取消喜欢失败，请稍后重试" : "喜欢失败，请稍后重试";
                RecordNeteaseLikeEvent($"失败：{(wasLiked ? "取消喜欢" : "喜欢")}请求未成功，参数 id={songId}&like={!wasLiked}");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Netease like request failed");
            NeteaseLikeButton.ToolTip = "网易云请求失败，请稍后重试";
            RecordNeteaseLikeEvent($"异常：{ex.GetType().Name} - {ex.Message}");
        }
        finally
        {
            NeteaseLikeButton.IsEnabled = true;
        }
    }

    private void RecordNeteaseLikeEvent(string message)
    {
        _lastNeteaseLikeEvent = message;
        _lastNeteaseLikeEventUtc = DateTime.Now;
        Logger.Debug("Netease like event: {0}", message);
    }

    private async Task UpdateNeteaseLikeStateAsync(string title, string artist, string songKey, CancellationToken cancellationToken)
    {
        try
        {
            if (!NeteaseMusicService.IsLoggedIn)
            {
                NeteaseLikeIcon.Opacity = 0.5;
                NeteaseLikeButton.ToolTip = "登录网易云后显示喜欢状态";
                return;
            }

            var songId = await NeteaseMusicService.ResolveSongIdAsync(title, artist, cancellationToken)
                ?? LyricsService.TryGetCurrentLocalSongId(title)
                ?? LyricsService.TryGetLastSongId(title, artist);
            var liked = songId is not null && await NeteaseMusicService.IsSongLikedAsync(songId.Value, cancellationToken);
            if (cancellationToken.IsCancellationRequested || !string.Equals(_neteaseLikeSongKey, songKey, StringComparison.Ordinal))
                return;
            if (!Dispatcher.CheckAccess())
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (string.Equals(_neteaseLikeSongKey, songKey, StringComparison.Ordinal))
                        UpdateNeteaseLikeVisual(liked);
                });
                return;
            }
            UpdateNeteaseLikeVisual(liked);
        }
        catch (OperationCanceledException)
        {
            // A song change cancels the stale state lookup.
        }
    }

    private static string BuildNeteaseSongKey(string title, string artist) =>
        $"{title.Trim()}\u001f{artist.Trim()}";

    private void UpdateNeteaseLikeVisual(bool liked)
    {
        NeteaseLikeIcon.Opacity = liked ? 1 : 0.5;
        NeteaseLikeIcon.Filled = liked;
        NeteaseLikeButton.ToolTip = liked ? "当前歌曲已喜欢" : "喜欢当前歌曲";
        taskbarWindow?.SetLikeVisual(liked);
    }

    private static bool IsNeteaseSession(MediaSession session)
    {
        var appId = session.Id ?? string.Empty;
        var appName = MediaPlayerData.GetAndCacheMediaPlayerData(appId).Item1 ?? appId;
        return appId.Contains("netease", StringComparison.OrdinalIgnoreCase)
            || appId.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase)
            || appName.Contains("网易云", StringComparison.OrdinalIgnoreCase)
            || appName.Contains("NetEase", StringComparison.OrdinalIgnoreCase)
            || appName.Contains("Cloud Music", StringComparison.OrdinalIgnoreCase);
    }

    private void SetNeteaseLikeVisibility(bool isNetease)
    {
        var mediaFlyoutVisible = isNetease && SettingsManager.Current.MediaFlyoutNeteaseLikeEnabled;
        var taskbarVisible = isNetease && SettingsManager.Current.TaskbarWidgetNeteaseLikeEnabled;
        NeteaseLikeButton.Visibility = mediaFlyoutVisible ? Visibility.Visible : Visibility.Collapsed;
        taskbarWindow?.SetLikeVisibility(taskbarVisible);
        UpdateUILayout();
    }

    private void LyricsDebug_Click(object sender, RoutedEventArgs e)
    {
#if DEBUG
        if (_lyricsDebugWindow is { IsVisible: true }) { _lyricsDebugWindow.Activate(); return; }
        _lyricsDebugWindow = new LyricsDebugWindow(() =>
        {
            var session = GetActiveMediaSession();
            var info = session is null ? "当前会话: 无" : TryGetMediaProperties(session.ControlSession) is { } p
                ? BuildLyricsDebugInfo(session, p.Title, p.Artist)
                : "当前会话: 无媒体属性";
            return info;
        }) { Owner = this };
        _lyricsDebugWindow.Closed += (_, _) => _lyricsDebugWindow = null;
        _lyricsDebugWindow.Show();
#else
        // The menu item is hidden in Release builds, but its XAML event binding
        // still needs a handler at compile time.
        return;
#endif
    }

#if DEBUG
    private string BuildLyricsDebugInfo(MediaSession session, string title, string artist)
    {
        var control = session.ControlSession;
        var media = TryGetMediaProperties(control);
        return $"SessionId = {session.Id}\n\n"
            + DumpObject("MediaProperties", media)
            + $"歌词歌曲 ID = {LyricsService.LastSongId}\n"
            + $"歌曲 ID 来源 = {LyricsService.LastSongLookupSource}\n"
            + $"找歌请求地址 = {LyricsService.LastSongLookupUrl}\n"
            + $"红心事件 = {_lastNeteaseLikeEventUtc:yyyy-MM-dd HH:mm:ss} {_lastNeteaseLikeEvent}\n"
            + $"LyricsParsedLineCount = {LyricsService.LastParsedLineCount}\nLyricsParsedFirstLine = {LyricsService.LastParsedFirstLine}\n";
    }

    private static string DumpObject(string name, object? value)
    {
        var lines = new List<string> { $"[{name}]" };
        DumpObjectProperties(value, lines, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
        lines.Add(string.Empty);
        return string.Join(Environment.NewLine, lines);
    }

    private static void DumpObjectProperties(object? value, List<string> lines, int depth, HashSet<object> visited)
    {
        if (value is null) { lines.Add("null"); return; }
        if (depth > 4 || value is string || value.GetType().IsPrimitive || value is DateTime || value is DateTimeOffset || value is TimeSpan || value.GetType().IsEnum)
        {
            lines.Add(value.ToString() ?? "null");
            return;
        }
        if (!visited.Add(value)) { lines.Add("<cycle>"); return; }
        foreach (var property in value.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0) continue;
            object? propertyValue;
            try { propertyValue = property.GetValue(value); }
            catch (Exception ex) { lines.Add($"{property.Name} = <{ex.GetType().Name}>"); continue; }
            lines.Add($"{new string(' ', (depth + 1) * 2)}{property.Name} =");
            DumpObjectProperties(propertyValue, lines, depth + 1, visited);
        }
    }
#endif

    public async Task ShowLyricsForActiveSessionAsync()
    {
        var session = GetActiveMediaSession();
        var properties = session is null ? null : TryGetMediaProperties(session.ControlSession);
        if (properties is null || string.IsNullOrWhiteSpace(properties.Title))
        {
            if (_lyricsWindow is not { IsVisible: true })
            {
                _lyricsWindow = new LyricsWindow("暂无正在播放的歌曲", "播放音乐后歌词会自动更新", null);
                _lyricsWindow.Closed += (_, _) => _lyricsWindow = null;
                _lyricsWindow.Show();
                WindowHelper.SetTopmost(_lyricsWindow);
            }
            return;
        }

        LyricsButton.IsEnabled = false;
        try
        {
            var neteaseSongId = ExtractNetEaseSongId(session.Id);
            var key = $"{properties.Title}\u001f{properties.Artist}";
            LyricsService.LyricsTrack? track;
            if (!_lyricsCache.TryGetValue(key, out track))
            {
                track = await LyricsService.GetLyricsTrackAsync(properties.Title, properties.Artist, neteaseSongId);
                _lyricsCache[key] = track;
            }
            if (_lyricsWindow is { IsVisible: true })
            {
                _lyricsWindow.Activate();
                return;
            }

            // Keep the lyrics view useful when the provider has no matching entry:
            // show the SMTC metadata instead of interrupting playback with a dialog.
            _lyricsWindow = new LyricsWindow(properties.Title, properties.Artist, track, () => GetNativePlaybackPosition(session));
            _lyricsWindow.Closed += (_, _) => _lyricsWindow = null;
            _lyricsWindow.Show();
            WindowHelper.SetTopmost(_lyricsWindow);
        }
        finally
        {
            LyricsButton.IsEnabled = true;
        }
    }

    private async Task UpdateFullscreenLyricsAsync()
    {
        var fullscreen = SettingsManager.Current.DesktopLyricsEnabled
            && SettingsManager.Current.DesktopLyricsAutoShow
            && FullscreenDetector.IsFullscreenApplicationActive();
        if (fullscreen == _fullscreenLyricsActive) return;
        _fullscreenLyricsActive = fullscreen;
        if (fullscreen)
        {
            if (_lyricsWindow is not { IsVisible: true })
            {
                _lyricsOpenedByFullscreen = true;
                await ShowLyricsForActiveSessionAsync();
            }
            return;
        }

        if (_lyricsOpenedByFullscreen && _lyricsWindow is { IsVisible: true })
            _lyricsWindow.Close();
        _lyricsOpenedByFullscreen = false;
    }

    private void SettingsManager_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UserSettings.MediaFlyoutNeteaseLikeEnabled)
            or nameof(UserSettings.TaskbarWidgetNeteaseLikeEnabled))
        {
            var session = GetActiveMediaSession();
            SetNeteaseLikeVisibility(session is not null && IsNeteaseSession(session));
            return;
        }

        if (e.PropertyName != nameof(SettingsManager.Current.DesktopLyricsEnabled)) return;
        if (!SettingsManager.Current.DesktopLyricsEnabled && _lyricsWindow is { IsVisible: true })
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_lyricsWindow is { IsVisible: true }) _lyricsWindow.Close();
                _lyricsOpenedByFullscreen = false;
                _fullscreenLyricsActive = false;
            });
        }
    }

    private async Task PreloadLyricsAsync(MediaSession session, string title, string artist)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        var key = $"{title}\u001f{artist}";
        if (_lyricsCacheKey == key && _activeLyricsTrack is not null) return;
        if (_lyricsCache.TryGetValue(key, out var cached) && cached is not null)
        {
            _lyricsCacheKey = key;
            _activeLyricsTrack = cached;
            _activeLyricsTitle = title;
            return;
        }
        _lyricsLoadCts?.Cancel();
        _lyricsLoadCts?.Dispose();
        _lyricsLoadCts = new CancellationTokenSource();
        var requestToken = _lyricsLoadCts.Token;
        _lyricsCacheKey = key;
        _activeLyricsTrack = null;
        _activeLyricsTitle = title;
        _estimatedPositionKey = session.Id + "|" + title;
        _estimatedPosition = TimeSpan.Zero;
        _estimatedPositionUpdatedUtc = DateTime.UtcNow;
        _lastLyricsAttemptUtc = DateTime.UtcNow;
        LyricsService.LyricsTrack? track;
        try
        {
            track = await LyricsService.GetLyricsTrackAsync(title, artist, ExtractNetEaseSongId(session.Id), requestToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (requestToken.IsCancellationRequested) return;
        if (track is not null) _lyricsCache[key] = track;
        if (_lyricsCacheKey == key)
        {
            _activeLyricsTrack = track;
            _activeLyricsTitle = title;
            if (_lyricsWindow is { IsVisible: true })
                _lyricsWindow.UpdateTrack(title, artist, track, () => GetNativePlaybackPosition(session));
            UpdateLyricsDisplay();
        }
    }

    private void UpdateDesktopLyricsWindow()
    {
        if (_lyricsWindow is not { IsVisible: true }) return;
        var session = GetActiveMediaSession();
        var properties = session is null ? null : TryGetMediaProperties(session.ControlSession);
        if (session is null || properties is null || string.IsNullOrWhiteSpace(properties.Title)) return;
        var key = $"{properties.Title}\u001f{properties.Artist}";
        if (_lyricsCacheKey != key)
        {
            _ = PreloadLyricsAsync(session, properties.Title, properties.Artist);
            return;
        }
        _lyricsWindow.UpdateTrack(properties.Title, properties.Artist, _activeLyricsTrack,
            () => GetNativePlaybackPosition(session));
    }

    private void UpdateAccentColorsIfNeeded(string title, string artist)
    {
        var key = title + "\u001f" + artist;
        if (key == _lastAccentMediaKey) return;
        _lastAccentMediaKey = key;
        BitmapHelper.GetDominantColors(1);
    }

    private void UpdateLyricsDisplay()
    {
        if (!SettingsManager.Current.TaskbarLyricsEnabled) return;
        var session = GetActiveMediaSession();
        if (session is null || _activeLyricsTrack is null || string.IsNullOrWhiteSpace(_activeLyricsTitle)) return;
        var line = _activeLyricsTrack.GetCurrentLine(GetNativePlaybackPosition(session));
        var text = string.IsNullOrWhiteSpace(line) ? _activeLyricsTitle : line;
        if (!string.Equals(text, _animatedLyricsText, StringComparison.Ordinal))
            AnimateLyricsText(text);
        taskbarWindow?.SetLyricsText(text);
    }

    private void AnimateLyricsText(string text)
    {
        _animatedLyricsText = text;
        _animatedLyricsIndex = 0;
        _lyricsWordTimer?.Stop();

        // Reveal words (or individual CJK characters when there are no spaces)
        // while giving the title a short vertical bounce on each lyric change.
        var tokens = text.Any(char.IsWhiteSpace)
            ? Regex.Matches(text, @"\s+|[^\s]+\s*").Select(match => match.Value).ToArray()
            : text.Select(character => character.ToString()).ToArray();
        SongTitle.Text = string.Empty;
        SongTitleOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -4, TimeSpan.FromMilliseconds(110))
        {
            AutoReverse = true,
            EasingFunction = new QuadraticEase()
        });
        _lyricsWordTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(55) };
        _lyricsWordTimer.Tick += (_, _) =>
        {
            if (_animatedLyricsIndex >= tokens.Length)
            {
                _lyricsWordTimer?.Stop();
                return;
            }
            SongTitle.Text += tokens[_animatedLyricsIndex++];
        };
        _lyricsWordTimer.Start();
    }

    private static TimeSpan GetPlaybackPosition(MediaSession session)
    {
        var timeline = session.ControlSession.GetTimelineProperties();
        var position = timeline.Position;
        if (session.ControlSession.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            position += DateTimeOffset.Now - timeline.LastUpdatedTime;
        return position < TimeSpan.Zero ? TimeSpan.Zero : position;
    }

    private TimeSpan GetNativePlaybackPosition(MediaSession fallbackSession)
    {
        try
        {
            var nativeSession = _nativeSmtcManager?.GetCurrentSession();
            if (nativeSession is not null)
            {
                var timeline = nativeSession.GetTimelineProperties();
                var position = timeline.Position;
                if (timeline.EndTime > TimeSpan.Zero)
                {
                    if (nativeSession.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        position += DateTimeOffset.Now - timeline.LastUpdatedTime;
                    return position < TimeSpan.Zero ? TimeSpan.Zero : position;
                }
            }
        }
        catch (Exception ex) { Logger.Debug(ex, "Native SMTC timeline unavailable"); }

        // Windows.Media.Control may be unavailable while the legacy session still
        // has a valid timeline, so use it before falling back to estimation.
        try
        {
            var fallbackTimeline = fallbackSession.ControlSession.GetTimelineProperties();
            if (fallbackTimeline.EndTime > TimeSpan.Zero)
            {
                var position = fallbackTimeline.Position;
                if (fallbackSession.ControlSession.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    position += DateTimeOffset.Now - fallbackTimeline.LastUpdatedTime;
                return position < TimeSpan.Zero ? TimeSpan.Zero : position;
            }
        }
        catch (Exception ex) { Logger.Debug(ex, "Fallback SMTC timeline unavailable"); }

        // The taskbar seek bar is refreshed from the same active session and can
        // still hold the latest position while a provider temporarily omits its
        // timeline end time. Use it before falling back to a local estimate.
        try
        {
            if (_seekBarEnabled && Seekbar.Value > 0)
                return TimeSpan.FromSeconds(Seekbar.Value);
        }
        catch (Exception ex) { Logger.Debug(ex, "Taskbar seekbar position unavailable"); }

        var properties = TryGetMediaProperties(fallbackSession.ControlSession);
        var key = fallbackSession.Id + "|" + (properties?.Title ?? string.Empty);
        var now = DateTime.UtcNow;
        if (!string.Equals(_estimatedPositionKey, key, StringComparison.Ordinal))
        {
            _estimatedPositionKey = key;
            _estimatedPosition = TimeSpan.Zero;
            _estimatedPositionUpdatedUtc = now;
        }

        var status = fallbackSession.ControlSession.GetPlaybackInfo()?.PlaybackStatus;
        if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
        {
            if (_estimatedPositionUpdatedUtc != default)
                _estimatedPosition += now - _estimatedPositionUpdatedUtc;
        }
        _estimatedPositionUpdatedUtc = now;
        return _estimatedPosition < TimeSpan.Zero ? TimeSpan.Zero : _estimatedPosition;
    }

    private async Task InitializeNativeSmtcAsync()
    {
        try
        {
            _nativeSmtcManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch (Exception ex) { Logger.Debug(ex, "Native SMTC manager initialization unavailable"); }
    }

    private static string? ExtractNetEaseSongId(string sessionId)
    {
        if (Regex.IsMatch(sessionId, "^\\d+$")) return sessionId;
        var match = Regex.Match(sessionId, "(?:netease|song|music)?[_-]?id[=:]?(\\d+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        var activeSession = GetActiveMediaSession();
        if (activeSession == null) return;

        await activeSession.ControlSession.TrySkipPreviousAsync();
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        var activeSession = GetActiveMediaSession();
        if (activeSession == null) return;

        await activeSession.ControlSession.TryTogglePlayPauseAsync();
    }

    private async void Forward_Click(object sender, RoutedEventArgs e)
    {
        var activeSession = GetActiveMediaSession();
        if (activeSession == null) return;

        await activeSession.ControlSession.TrySkipNextAsync();
    }

    private async void Repeat_Click(object sender, RoutedEventArgs e)
    {
        var activeSession = GetActiveMediaSession();
        if (activeSession == null) return;

        if (activeSession.ControlSession.GetPlaybackInfo().AutoRepeatMode == global::Windows.Media.MediaPlaybackAutoRepeatMode.None)
        {
            SymbolRepeat.Dispatcher.Invoke(() => SymbolRepeat.Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowRepeatAll24);
            await activeSession.ControlSession.TryChangeAutoRepeatModeAsync(global::Windows.Media.MediaPlaybackAutoRepeatMode.List);
        }
        else if (activeSession.ControlSession.GetPlaybackInfo().AutoRepeatMode == global::Windows.Media.MediaPlaybackAutoRepeatMode.List)
        {
            SymbolRepeat.Dispatcher.Invoke(() => SymbolRepeat.Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowRepeat124);
            await activeSession.ControlSession.TryChangeAutoRepeatModeAsync(global::Windows.Media.MediaPlaybackAutoRepeatMode.Track);
        }
        else if (activeSession.ControlSession.GetPlaybackInfo().AutoRepeatMode == global::Windows.Media.MediaPlaybackAutoRepeatMode.Track)
        {
            SymbolRepeat.Dispatcher.Invoke(() => SymbolRepeat.Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowRepeatAllOff24);
            await activeSession.ControlSession.TryChangeAutoRepeatModeAsync(global::Windows.Media.MediaPlaybackAutoRepeatMode.None);
        }
    }

    private async void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        var activeSession = GetActiveMediaSession();
        if (activeSession == null) return;

        if (activeSession.ControlSession.GetPlaybackInfo().IsShuffleActive == true)
        {
            SymbolShuffle.Dispatcher.Invoke(() => SymbolShuffle.Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowShuffleOff24);
            await activeSession.ControlSession.TryChangeShuffleActiveAsync(false);
        }
        else
        {
            SymbolShuffle.Dispatcher.Invoke(() => SymbolShuffle.Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowShuffle24);
            await activeSession.ControlSession.TryChangeShuffleActiveAsync(true);
        }
    }

    private void Seekbar_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging) return;
        _isDragging = true;

        Slider slider = (Slider)sender;
        System.Windows.Point clickPosition = e.GetPosition(slider);
        double thumbWidth = slider.Template.FindName("Thumb", slider) is Thumb thumb ? thumb.ActualWidth : 0;
        double ratio = (clickPosition.X - thumbWidth / 2) / (slider.ActualWidth - thumbWidth);
        ratio = Math.Max(0, Math.Min(1, ratio));
        double targetSeconds = ratio * slider.Maximum;
        // Bug: if the position is 0, then it will cause the position to not change when changing playback position
        if (targetSeconds == 0) targetSeconds = 1;
        Dispatcher.Invoke(() =>
        {
            Seekbar.Value = TimeSpan.FromSeconds(targetSeconds).TotalSeconds;
        });
    }

    private async void Seekbar_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (GetActiveMediaSession() is { } session)
        {
            var seekPosition = TimeSpan.FromSeconds(Seekbar.Value);
            if (seekPosition == TimeSpan.Zero) seekPosition = TimeSpan.FromSeconds(1);
            await session.ControlSession.TryChangePlaybackPositionAsync(seekPosition.Ticks);
        }
        _isDragging = false;
    }

    private void Seekbar_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isDragging) return;
        var timespan = TimeSpan.FromSeconds(e.NewValue);
        Dispatcher.Invoke(() =>
        {
            SeekbarCurrentDuration.Text = timespan.ToString(timespan.Hours > 0 ? @"hh\:mm\:ss" : @"mm\:ss");
        });
    }

    private void SeekbarUpdateUi(object? sender)
    {
        if (DateTime.Now.Subtract(_lastSelfUpdateTimestamp).TotalSeconds < 1) return;

        if (!_seekBarEnabled || Visibility != Visibility.Visible || _isDragging) return;
        if (GetActiveMediaSession() is not { } session) return;

        var timeline = session.ControlSession.GetTimelineProperties();
        var pos = timeline.Position + (DateTime.Now - timeline.LastUpdatedTime.DateTime);
        if (pos > timeline.EndTime)
        {
            HandlePlayBackState(GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed);
            return;
        }

        UpdateSeekbarCurrentDuration(pos);
    }

    private void UpdateSeekbarCurrentDuration(TimeSpan pos)
    {
        Dispatcher.Invoke(() =>
        {
            Seekbar.Value = pos.TotalSeconds;
            SeekbarCurrentDuration.Text = pos.ToString(pos.Hours > 0 ? @"hh\:mm\:ss" : @"mm\:ss");
        });
    }

    private void HandlePlayBackState(GlobalSystemMediaTransportControlsSessionPlaybackStatus? status)
    {
        if (status == null) return;
        if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
        {
            if (_isActive) return;
            _isActive = true;
            _positionTimer.Change(0, _seekbarUpdateInterval);
        }
        else
        {
            if (!_isActive) return;
            _isActive = false;
            _positionTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private void CleanupResources()
    {
        // try saving settings before exiting if window is still open
        // disabled because it caused too many issues (race conditions, shutdown exceptions), could look into another time
        //try
        //{
        //    SettingsManager.SaveSettings();
        //    Logger.Info("Settings saved successfully on cleanup");
        //}
        //catch (Exception ex)
        //{
        //    Logger.Error(ex, "Error while saving settings on cleanup");
        //}

        // should be handled automatically on app exit but just in case
        try
        {
            _isCleaningUp = true;
            _displayRefreshTimer.Stop();
            _displayRefreshTimer.Tick -= DisplayRefreshTimer_Tick;
            _mediaSelectionTimer.Stop();

            // unsubscribe from events
            mediaManager.OnAnyMediaPropertyChanged -= MediaManager_OnAnyMediaPropertyChanged;
            mediaManager.OnAnyPlaybackStateChanged -= CurrentSession_OnPlaybackStateChanged;
            mediaManager.OnAnyTimelinePropertyChanged -= MediaManager_OnAnyTimelinePropertyChanged;
            mediaManager.OnAnySessionClosed -= MediaManager_OnAnySessionClosed;
            SettingsManager.Current.PropertyChanged -= SettingsManager_PropertyChanged;

            // dispose managed resources
            _positionTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _positionTimer?.Dispose();
            cts?.Cancel();
            cts?.Dispose();

            TaskbarVisualizerControl.DisposeVisualizer();

            // unhook hooks
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }

            DeregisterShellHookWindow(new WindowInteropHelper(this).Handle);

            // clean up other resources
            if (lockWindow?.IsLoaded == true)
                lockWindow.Close();

            if (nextUpWindow?.IsLoaded == true)
                nextUpWindow.Close();

            if (taskbarWindow?.IsLoaded == true)
                taskbarWindow.Close();

            if (volumeMixerWindow?.IsLoaded == true)
                volumeMixerWindow.Close();

            if (_lyricsWindow?.IsLoaded == true)
                _lyricsWindow.Close();

            // restore native volume OSD
            VolumeMixerWindow.ShowVolumeOsd();

            // dispose mutex
            singleton?.Dispose();

            // flush and close NLog
            NLog.LogManager.Shutdown();
        }
        catch (ObjectDisposedException)
        {
            // harmless shutdown exceptions
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            CleanupResources();
        }
        finally
        {
            base.OnClosed(e);
        }
    }

    private void MicaWindow_MouseEnter(object sender, MouseEventArgs e) // keep the flyout open when mouse is over
    {
        ShowMediaFlyout();
    }

    private void NotifyIconQuit_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CleanupResources();
        }
        finally
        {
            Application.Current.Shutdown();
        }
    }

    private async Task<bool> WaitForExplorerReadyAsync(int timeoutMs = 60000)
    {
        var sw = Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar != IntPtr.Zero &&
                GetWindowRect(taskbar, out NativeMethods.RECT rect) &&
                rect.Right > rect.Left &&
                rect.Bottom > rect.Top)
            {
                return true; // taskbar exists and has geometry
            }

            await Task.Delay(200);
        }

        return false;
    }

    private void ScheduleDisplayEnvironmentRefresh(string reason)
    {
        if (_isCleaningUp)
            return;

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ScheduleDisplayEnvironmentRefresh(reason));
            return;
        }

        _pendingDisplayRefreshReason = reason;
        _displayRefreshTimer.Stop();
        _displayRefreshTimer.Start();
        Logger.Debug($"Scheduled display environment refresh: {reason}");
    }

    private void DisplayRefreshTimer_Tick(object? sender, EventArgs e)
    {
        _displayRefreshTimer.Stop();

        if (_displayRefreshInProgress || _isCleaningUp)
            return;

        _displayRefreshInProgress = true;
        try
        {
            RefreshDisplayEnvironment(_pendingDisplayRefreshReason);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to refresh windows after a display environment change");
        }
        finally
        {
            _displayRefreshInProgress = false;
        }
    }

    private void RefreshDisplayEnvironment(string reason)
    {
        var monitors = MonitorUtil.GetMonitors();
        if (monitors.Count == 0)
        {
            Logger.Warn($"Display environment refresh skipped because no monitors were found ({reason})");
            return;
        }

        Logger.Info($"Refreshing window DPI and placement after display change ({reason}); monitors={monitors.Count}");

        // Stop placement animations that were calculated for the previous work area.
        cts.Cancel();
        _isHiding = true;
        Hide();

        // Move the reusable main flyout to its current target monitor while hidden. This
        // makes WPF process the per-monitor DPI transition before the next animation.
        var targetMonitor = getSelectedMonitor();
        WindowHelper.SetPosition(this, targetMonitor.workArea.Left, targetMonitor.workArea.Top);
        InvalidateMeasure();
        InvalidateArrange();
        InvalidateVisual();
        UpdateLayout();
        SyncMainFlyoutSizeToCurrentDpi(new WindowInteropHelper(this).Handle);

        // These windows retain an HWND for their lifetime. Recreate them so they cannot
        // keep DPI, work-area, taskbar-parent, or UI Automation state from the old topology.
        if (lockWindow != null)
        {
            lockWindow.Close();
            lockWindow = null;
        }

        if (nextUpWindow != null)
        {
            nextUpWindow.Close();
            nextUpWindow = null;
        }

        if (volumeMixerWindow != null)
        {
            volumeMixerWindow.Close();
            volumeMixerWindow = new VolumeMixerWindow();
        }

        RecreateTaskbarWindow();
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        // detect key presses from both keyboard hook and shell hook to show flyouts
        if (msg == WM_SHELLHOOK && wParam == HSHELL_APPCOMMAND)
        {
            int highWord = (int)(lParam >> 16);
            int cmd = highWord & 0x0FFF;
            int device = highWord & 0xF000;

            bool isMediaCommand = cmd switch
            {
                APPCOMMAND_MEDIA_PLAY_PAUSE => true,
                APPCOMMAND_MEDIA_NEXTTRACK => true,
                APPCOMMAND_MEDIA_PREVIOUSTRACK => true,
                APPCOMMAND_MEDIA_STOP => true,
                _ => false
            };

            bool isVolumeCommand = false;

            if (!isMediaCommand && !SettingsManager.Current.MediaFlyoutVolumeKeysExcluded)
            {
                isVolumeCommand = cmd switch
                {
                    APPCOMMAND_VOLUME_MUTE => true,
                    APPCOMMAND_VOLUME_DOWN => true,
                    APPCOMMAND_VOLUME_UP => true,
                    _ => false
                };
            }

            if (!isMediaCommand && !isVolumeCommand)
                return 0;

            bool isKeyCommand = device == FAPPCOMMAND_KEY;

            if (!isKeyCommand)
                return 0;

            bool result = TryShowMediaFlyoutDebounced();

            if (!result)
            {
                return 0;
            }

            handled = true;
        }
        else if (msg == WM_TASKBARCREATED)
        {
            Logger.Warn("Explorer restart detected (TaskbarCreated)");

            ExplorerRestarting = true;

            // Defer recovery, do NOT touch tray/taskbar immediately
            Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    // Wait for Explorer to actually stabilize
                    if (await WaitForExplorerReadyAsync())
                    {
                        ExplorerRestarting = false;
                        Logger.Info("Explorer stabilized, resuming taskbar integration");

                        // Now it is safe to recreate tray icon
                        RecreateTrayIconSafely();
                    }
                    else
                    {
                        Logger.Warn("Explorer did not stabilize within timeout; keeping integration disabled");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Explorer recovery failed");
                }
            }, DispatcherPriority.Background);

            handled = true;
            return 0;
        }
        else if (msg == WM_DISPLAYCHANGE)
        {
            ScheduleDisplayEnvironmentRefresh("WM_DISPLAYCHANGE");
            return 0;
        }
        else if (msg == WM_DPICHANGED)
        {
            // Leave the message unhandled so WPF can update its internal DPI state,
            // then size the native HWND from the logical WPF dimensions exactly once.
            Dispatcher.BeginInvoke(() =>
            {
                InvalidateMeasure();
                InvalidateArrange();
                InvalidateVisual();
                UpdateLayout();
                SyncMainFlyoutSizeToCurrentDpi(hwnd);
            }, DispatcherPriority.Loaded);
            return 0;
        }
        else if (msg == WM_SETTINGCHANGE) // system settings changed
        {
            if (wParam.ToInt64() == SPI_SETWORKAREA)
            {
                ScheduleDisplayEnvironmentRefresh("WM_SETTINGCHANGE/SPI_SETWORKAREA");
                return 0;
            }

            string? changedSetting = lParam == IntPtr.Zero ? null : Marshal.PtrToStringUni(lParam);
            if (changedSetting == "SystemDockMode")
            {
                ScheduleDisplayEnvironmentRefresh($"WM_SETTINGCHANGE/{changedSetting}");
                return 0;
            }

            // check if the changed setting is related to theme or accent color
            if (changedSetting != "ImmersiveColorSet" && changedSetting != "WindowsThemeElement")
                return 0;

            Logger.Info($"System setting changed: {changedSetting}, from {msg}");

            try
            {
                // update theme for taskbar widget since it's independent from the main app theme
                ThemeManager.UpdateTaskbarWidget();
                // update Acrylic windows background colors
                WindowBlurHelper.AdjustBlurOpacityForAllWindows(SettingsManager.Current.AcrylicBlurOpacity);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to apply theme changes to taskbar widgets or Acrylic windows");
            }
            return 0;
        }

        return 0;
    }

    private void SyncMainFlyoutSizeToCurrentDpi(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;

        uint dpi = GetDpiForWindow(hwnd);
        if (dpi == 0)
            return;

        double dpiScale = dpi / 96.0;
        int pixelWidth = (int)Math.Ceiling(Width * dpiScale);
        int pixelHeight = (int)Math.Ceiling(Height * dpiScale);

        if (!SetWindowPos(
            hwnd,
            0,
            0,
            0,
            pixelWidth,
            pixelHeight,
            SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE))
        {
            Logger.Warn($"Failed to resize MainWindow for DPI; HWND=0x{hwnd.ToInt64():X}, DPI={dpi}, Size={pixelWidth}x{pixelHeight}, Win32Error={Marshal.GetLastWin32Error()}");
        }
    }

    private void RecreateTrayIconSafely()
    {
        try
        {
            nIcon.Visibility = Visibility.Collapsed;

            if (!SettingsManager.Current.NIconHide)
            {
                nIcon.Visibility = Visibility.Visible;
                nIcon.Register();
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to recreate tray icon safely");
        }
    }

    private async void MicaWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Hide();
        UpdateUILayout();
        ThemeManager.ApplySavedTheme();

        // add tray icon hook when taskbar resets
        try
        {
            HwndSource? source = PresentationSource.FromVisual(this) as HwndSource;
            if (source != null)
            {
                source.AddHook(WndProc);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to initialize tray icon");
        }

        try
        {
            await LicenseManager.Instance.InitializeAsync();

            // Sync license status from LicenseManager to SettingsManager
            SettingsManager.Current.IsPremiumUnlocked = LicenseManager.Instance.IsPremiumUnlocked;
            SettingsManager.Current.IsStoreVersion = LicenseManager.Instance.IsStoreVersion;
            SettingsManager.SaveSettings();

            Logger.Info($"License synced on startup - Store: {SettingsManager.Current.IsStoreVersion}, Premium: {SettingsManager.Current.IsPremiumUnlocked}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to initialize license");
        }

        // Add the experiments loading here
        await ExperimentsService.GetExperimentsAsync();

        BitmapHelper.GetDominantColors(1);
        taskbarWindow = new TaskbarWindow();
        UpdateTaskbar();
        volumeMixerWindow = new VolumeMixerWindow();
    }

    public void RecreateTaskbarWindow()
    {
        try
        {
            Logger.Info("Recreating Taskbar Widget window");

            if (taskbarWindow != null)
            {
                try
                {
                    taskbarWindow.Close();
                }
                catch { }

                taskbarWindow = null;
            }

            taskbarWindow = new();
            UpdateTaskbar();

            Logger.Info("Taskbar Widget window recreated successfully");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to recreate Taskbar Widget window");
        }
    }

    private void nIcon_LeftClick(Wpf.Ui.Tray.Controls.NotifyIcon sender, RoutedEventArgs e) // change the behavior of the tray icon
    {
        if (SettingsManager.Current.NIconLeftClick == 0)
        {
            openSettings(sender, e);
            //Wpf.Ui.Appearance.ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Mica); // to change the theme
            //ThemeService themeService = new ThemeService();
            //themeService.ChangeTheme(MicaWPF.Core.Enums.WindowsTheme.Light);
        }
        else if (SettingsManager.Current.NIconLeftClick == 1) ShowMediaFlyout();
    }

    private Task PauseOtherSessions(MediaSession currentMediaSession)
    {
        return Task.WhenAll(
            mediaManager.CurrentMediaSessions.Values.Select(session =>
            {
                if (
                    session.Id != currentMediaSession.Id &&
                    session.ControlSession.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                )
                {
                    return session.ControlSession.TryPauseAsync().AsTask();
                }
                return Task.CompletedTask;
            })
        );
    }
    internal void ToggleBlur()
    {
        if (SettingsManager.Current.MediaFlyoutAcrylicWindowEnabled)
        {
            WindowBlurHelper.EnableBlur(this);
        }
        else
        {
            WindowBlurHelper.DisableBlur(this);
        }
    }

    private void MediaFlyoutCloseButton_Click(object sender, RoutedEventArgs e)
    {
        // Use the updated ShowMediaFlyout method with toggle mode to close the flyout
        ShowMediaFlyout(toggleMode: true);
    }
}
