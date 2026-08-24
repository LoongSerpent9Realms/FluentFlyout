using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Http;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using FluentFlyout.PluginApi;

namespace FoliaMajorBridge;

public sealed class Entry : IFluentFlyoutPlugin
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private IPluginContext? _context;
    private BridgeSettings _settings = new();
    private string? _sessionId;
    private bool _hasPublishedLyrics;

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        _context = context;
        Directory.CreateDirectory(context.DataDirectory);
        _settings = LoadSettings(context.DataDirectory);
        context.RegisterMediaEventHandler(HandleMediaEventAsync);
        context.RegisterPage(new PluginPage("folia-major-bridge-settings", "Folia Major", CreateSettingsView));
        return Task.CompletedTask;
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await ClearStageAsync(cancellationToken);
        _client.Dispose();
        _requestGate.Dispose();
    }

    private async Task HandleMediaEventAsync(PluginMediaEvent mediaEvent)
    {
        if (!_settings.Enabled) return;

        if (mediaEvent.Kind == "media-properties-changed")
        {
            _sessionId = mediaEvent.AppUserModelId;
            return;
        }

        if (mediaEvent.Kind == "session-closed")
        {
            if (_sessionId is null || mediaEvent.AppUserModelId is null || mediaEvent.AppUserModelId == _sessionId)
                await ClearStageAsync();
            return;
        }

        if (mediaEvent.Kind != "lyrics-updated") return;
        _sessionId = mediaEvent.AppUserModelId ?? _sessionId;
        if (string.IsNullOrWhiteSpace(mediaEvent.LyricsText))
        {
            await ClearStageAsync();
            return;
        }

        await PushLyricsAsync(mediaEvent);
    }

    private async Task PushLyricsAsync(PluginMediaEvent mediaEvent, CancellationToken cancellationToken = default)
    {
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            using var request = CreateRequest(HttpMethod.Post, "/stage/lyrics");
            request.Content = JsonContent.Create(new StageLyricsRequest(
                mediaEvent.Title,
                mediaEvent.Artist,
                new LocalLyricSource(mediaEvent.LyricsText!, mediaEvent.LyricsFormat ?? "lrc")), options: JsonOptions);
            using var response = await _client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException($"Folia Stage returned {(int)response.StatusCode}: {detail}");
            }
            _hasPublishedLyrics = true;
            _context?.Log($"已推送歌词到 Folia Major：{mediaEvent.Title}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _context?.Log("推送歌词到 Folia Major 失败。请确认 Folia 已开启 Stage Mode、地址和 Token 正确。", ex);
        }
        finally { _requestGate.Release(); }
    }

    private async Task ClearStageAsync(CancellationToken cancellationToken = default)
    {
        if (!_hasPublishedLyrics || !_settings.Enabled) return;
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            using var request = CreateRequest(HttpMethod.Delete, "/stage/state");
            using var response = await _client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                _context?.Log($"清理 Folia Stage 状态失败：HTTP {(int)response.StatusCode}");
            else
                _hasPublishedLyrics = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { _context?.Log("清理 Folia Stage 状态失败。", ex); }
        finally { _requestGate.Release(); }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var baseUrl = _settings.Endpoint.TrimEnd('/');
        var request = new HttpRequestMessage(method, baseUrl + path);
        if (!string.IsNullOrWhiteSpace(_settings.Token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.Token.Trim());
        return request;
    }

    private FrameworkElement CreateSettingsView()
    {
        var endpoint = new TextBox { Text = _settings.Endpoint, Margin = new Thickness(0, 4, 0, 12) };
        var token = new PasswordBox { Password = _settings.Token, Margin = new Thickness(0, 4, 0, 12) };
        var enabled = new CheckBox { Content = "启用歌词推送", IsChecked = _settings.Enabled, Margin = new Thickness(0, 0, 0, 16) };
        var save = new Button { Content = "保存", Padding = new Thickness(16, 6, 16, 6), HorizontalAlignment = HorizontalAlignment.Left };
        var status = new TextBlock { Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap };
        save.Click += (_, _) =>
        {
            _settings = new BridgeSettings
            {
                Endpoint = string.IsNullOrWhiteSpace(endpoint.Text) ? "http://127.0.0.1:32107" : endpoint.Text.Trim(),
                Token = token.Password.Trim(),
                Enabled = enabled.IsChecked == true
            };
            SaveSettings(_context!.DataDirectory, _settings);
            status.Text = "已保存。请在 Folia Major 中开启 Stage Mode。";
        };
        var panel = new StackPanel { Margin = new Thickness(24), MaxWidth = 620 };
        panel.Children.Add(new TextBlock { Text = "Folia Major Stage", FontSize = 24, Margin = new Thickness(0, 0, 0, 18) });
        panel.Children.Add(new TextBlock { Text = "Stage 地址" }); panel.Children.Add(endpoint);
        panel.Children.Add(new TextBlock { Text = "Bearer Token" }); panel.Children.Add(token);
        panel.Children.Add(enabled); panel.Children.Add(save); panel.Children.Add(status);
        return new ScrollViewer { Content = panel };
    }

    private static BridgeSettings LoadSettings(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "settings.json");
            if (File.Exists(path)) return JsonSerializer.Deserialize<BridgeSettings>(File.ReadAllText(path), JsonOptions) ?? new BridgeSettings();
        }
        catch { }
        return new BridgeSettings();
    }

    private static void SaveSettings(string directory, BridgeSettings settings) =>
        File.WriteAllText(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(settings, JsonOptions));

    private sealed class BridgeSettings
    {
        public bool Enabled { get; set; }
        public string Endpoint { get; set; } = "http://127.0.0.1:32107";
        public string Token { get; set; } = "";
    }

    private sealed record StageLyricsRequest(string? Title, string? Artist, LocalLyricSource LyricSource);
    private sealed record LocalLyricSource(string Type, string LrcContent, string FormatHint)
    {
        [JsonConstructor]
        public LocalLyricSource(string lrcContent, string formatHint) : this("local", lrcContent, formatHint) { }
    }
}
