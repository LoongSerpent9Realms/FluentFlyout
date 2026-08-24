// Copyright (c) 2024-2026 The PulseFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net.Http.Json;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using FluentFlyout.Classes.Settings;

namespace FluentFlyoutWPF.Classes.Services;

/// <summary>Looks up lyrics through the Enhanced NetEase API, with LRCLIB as a fallback.</summary>
public static class LyricsService
{
    private const string DefaultApiBaseUrl = "https://music.loongst.com/";
    private static readonly HttpClient Client = CreateClient();
    public static string LastRequestUrl { get; private set; } = "";
    public static string LastStatus { get; private set; } = "未请求";
    public static string LastError { get; private set; } = "";
    public static int LastLyricsLength { get; private set; }
    public static int LastParsedLineCount { get; private set; }
    public static string LastParsedFirstLine { get; private set; } = "";
    public static long? LastSongId { get; private set; }
    public static string LastSongKey { get; private set; } = "";
    public static string LastSongLookupUrl { get; private set; } = "";
    public static string LastSongLookupSource { get; private set; } = "";

    // Shares the local player ID lookup with actions that must address a song
    // by ID (for example the authenticated like endpoint).
    public static long? TryGetCurrentLocalSongId(string title) => TryGetLocalNetEaseSongId(title);

    public static long? TryGetLastSongId(string title, string artist)
    {
        var key = BuildSongKey(title, artist);
        return string.Equals(LastSongKey, key, StringComparison.OrdinalIgnoreCase) ? LastSongId : null;
    }

    public static async Task<string?> GetLyricsAsync(string title, string artist, string? neteaseSongId = null, CancellationToken cancellationToken = default)
    {
        if (TryParseNetEaseSongId(neteaseSongId, out _)) { LastSongLookupSource = "媒体会话传入歌曲 ID"; LastSongLookupUrl = ""; }
        long? parsedId;
        if (TryParseNetEaseSongId(neteaseSongId, out var suppliedId)) parsedId = suppliedId;
        else
        {
            parsedId = TryGetLocalNetEaseSongId(title);
            if (parsedId is not null) { LastSongLookupSource = "本地网易云 playingList"; LastSongLookupUrl = "本地文件 playingList"; }
            parsedId ??= await SearchNetEaseSongIdAsync(title, artist, cancellationToken);
        }
        SetLastSongId(parsedId > 0 ? parsedId : null, title, artist);

        if (parsedId is > 0)
        {
            var vkeysLyrics = await GetVKeysLyricsAsync(parsedId.Value, cancellationToken);
            if (!string.IsNullOrWhiteSpace(vkeysLyrics)) return vkeysLyrics;
        }

        if (string.IsNullOrWhiteSpace(title)) return null;

        var query = $"track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist ?? string.Empty)}";
        var result = await GetAsync($"https://lrclib.net/api/get?{query}", cancellationToken);
        if (result is null)
        {
            try
            {
                var candidates = await Client.GetFromJsonAsync<List<LrcLibResult>>($"https://lrclib.net/api/search?{query}", cancellationToken);
                result = candidates?.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.PlainLyrics)
                    || !string.IsNullOrWhiteSpace(candidate.SyncedLyrics));
            }
            catch (HttpRequestException) { return null; }
            catch (TaskCanceledException) { return null; }
        }

        if (result is null) return null;
        if (!string.IsNullOrWhiteSpace(result.SyncedLyrics)) return StripTimestamps(result.SyncedLyrics);
        return string.IsNullOrWhiteSpace(result.PlainLyrics) ? null : result.PlainLyrics.Trim();
    }

    public static async Task<LyricsTrack?> GetLyricsTrackAsync(string title, string artist, string? neteaseSongId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        LastError = "";
        LastLyricsLength = 0;
        LastParsedLineCount = 0;
        LastParsedFirstLine = "";
        if (TryParseNetEaseSongId(neteaseSongId, out _)) { LastSongLookupSource = "媒体会话传入歌曲 ID"; LastSongLookupUrl = ""; }
        long? parsedId;
        if (TryParseNetEaseSongId(neteaseSongId, out var suppliedId)) parsedId = suppliedId;
        else
        {
            parsedId = TryGetLocalNetEaseSongId(title);
            if (parsedId is not null) { LastSongLookupSource = "本地网易云 playingList"; LastSongLookupUrl = "本地文件 playingList"; }
            parsedId ??= await SearchNetEaseSongIdAsync(title, artist, cancellationToken);
        }
        SetLastSongId(parsedId > 0 ? parsedId : null, title, artist);

        if (parsedId is > 0)
        {
            var raw = await GetVKeysRawLyricsAsync(parsedId.Value, cancellationToken);
            var track = ParseTrack(raw);
            if (track != null) return track;
        }

        var query = $"track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist ?? string.Empty)}";
        var result = await GetAsync($"https://lrclib.net/api/get?{query}", cancellationToken);
        if (result == null)
        {
            try
            {
                var candidates = await Client.GetFromJsonAsync<List<LrcLibResult>>($"https://lrclib.net/api/search?{query}", cancellationToken);
                result = candidates?.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.SyncedLyrics) || !string.IsNullOrWhiteSpace(x.PlainLyrics));
            }
            catch (HttpRequestException) { return null; }
            catch (TaskCanceledException) { return null; }
            catch (JsonException) { return null; }
        }
        if (result == null) return null;
        return ParseTrack(result.SyncedLyrics) ?? ParseTrack(result.PlainLyrics);
    }

    private static async Task<long?> SearchNetEaseSongIdAsync(string title, string artist, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        try
        {
            var keywords = Uri.EscapeDataString($"{title} {artist}".Trim());
            LastRequestUrl = $"{GetApiBaseUrl()}search?keywords={keywords}&limit=10";
            LastSongLookupUrl = LastRequestUrl;
            LastSongLookupSource = "网易云搜索 API";
            LastStatus = "网易云搜索请求中";
            LastError = "";
            using var response = await GetNeteaseResponseAsync(LastRequestUrl, cancellationToken);
            LastStatus = $"网易云搜索 HTTP {(int)response.StatusCode}";
            if (!response.IsSuccessStatusCode) return null;

            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("result", out var result)
                || !result.TryGetProperty("songs", out var songs)
                || songs.ValueKind != JsonValueKind.Array) return null;

            var candidates = songs.EnumerateArray().Select((song, index) =>
            {
                var name = song.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                var artists = song.TryGetProperty("artists", out var a) && a.ValueKind == JsonValueKind.Array
                    ? string.Join(" ", a.EnumerateArray().Select(x => x.TryGetProperty("name", out var an) ? an.GetString() : null).Where(x => !string.IsNullOrWhiteSpace(x)))
                    : string.Empty;
                var score = ScoreSong(name, artists, title, artist);
                return new { song, index, score };
            }).OrderByDescending(x => x.score).ThenBy(x => x.index);
            foreach (var candidate in candidates)
            {
                // Do not use an arbitrary first search result. A wrong ID means wrong lyrics
                // and can also make the authenticated favourite state point to another song.
                if (candidate.score < 200) continue;
                if (candidate.song.TryGetProperty("id", out var id) && id.TryGetInt64(out var songId)) return songId;
            }
            return null;
        }
        catch (HttpRequestException ex) { LastStatus = "网易云搜索失败"; LastError = ex.Message; return null; }
        catch (TaskCanceledException) { LastStatus = "网易云搜索已取消"; return null; }
        catch (JsonException ex) { LastStatus = "网易云搜索 JSON 解析失败"; LastError = ex.Message; return null; }
    }

    private static int ScoreSong(string name, string songArtist, string title, string artist)
    {
        var normalizedName = Normalize(name);
        var normalizedTitle = Normalize(title);
        var score = normalizedName == normalizedTitle ? 200 : 0;
        if (score == 0 && StripTitleQualifier(normalizedName) == StripTitleQualifier(normalizedTitle)) score = 160;
        if (score == 0 && (normalizedName.Contains(normalizedTitle, StringComparison.OrdinalIgnoreCase)
            || normalizedTitle.Contains(normalizedName, StringComparison.OrdinalIgnoreCase))) score = 80;

        var requestedArtists = SplitArtists(artist);
        var matchedArtist = requestedArtists.Any(requested => SplitArtists(songArtist).Any(candidate =>
            candidate == requested || candidate.Contains(requested, StringComparison.OrdinalIgnoreCase)
            || requested.Contains(candidate, StringComparison.OrdinalIgnoreCase)));
        if (matchedArtist) score += 50;
        if (name.Contains("伴奏", StringComparison.OrdinalIgnoreCase) || name.Contains("纯音乐", StringComparison.OrdinalIgnoreCase)
            || name.Contains("instrumental", StringComparison.OrdinalIgnoreCase)) score -= 100;
        return score;
    }

    private static string Normalize(string value) => value.Trim().Replace("（", "(").Replace("）", ")").ToLowerInvariant();

    private static string StripTitleQualifier(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, @"\s*\([^)]*\)", string.Empty).Trim();

    private static IEnumerable<string> SplitArtists(string value) =>
        System.Text.RegularExpressions.Regex.Split(Normalize(value), @"[\/／、,&，;；]+")
            .Select(x => x.Trim()).Where(x => x.Length > 0);

    private static string BuildSongKey(string title, string artist) =>
        $"{title.Trim()}\u001f{artist.Trim()}";

    private static void SetLastSongId(long? songId, string title, string artist)
    {
        LastSongId = songId;
        LastSongKey = songId is > 0 ? BuildSongKey(title, artist) : "";
    }

    private static long? TryGetLocalNetEaseSongId(string title)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        };
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(root, "Netease", "CloudMusic", "webdata", "file", "playingList");
            if (!File.Exists(path)) continue;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var document = JsonDocument.Parse(stream);
                if (!document.RootElement.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in list.EnumerateArray())
                {
                    if (!item.TryGetProperty("track", out var track) || track.ValueKind != JsonValueKind.Object) continue;
                    if (!track.TryGetProperty("name", out var name) || !string.Equals(name.GetString(), title, StringComparison.OrdinalIgnoreCase)) continue;
                    if (track.TryGetProperty("id", out var id) && TryGetSongId(id, out var songId) && songId > 0)
                    {
                        LastStatus = $"网易云本地 playingList 命中 ID {songId}";
                        return songId;
                    }
                }
            }
            catch (Exception ex) { LastError = $"playingList: {ex.Message}"; }
        }
        return null;
    }

    private static bool TryGetSongId(JsonElement value, out long songId)
    {
        songId = 0;
        if (value.ValueKind == JsonValueKind.Number)
            return value.TryGetInt64(out songId);
        if (value.ValueKind == JsonValueKind.String)
            return long.TryParse(value.GetString(), out songId);
        return false;
    }

    private static async Task<string?> GetVKeysLyricsAsync(long songId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await GetNeteaseResponseAsync($"{GetApiBaseUrl()}lyric?id={songId}", cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var root = document.RootElement;
            var lyricText = FindLyricText(root);
            return string.IsNullOrWhiteSpace(lyricText) ? null : StripTimestamps(lyricText);
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static async Task<string?> GetVKeysRawLyricsAsync(long songId, CancellationToken cancellationToken)
    {
        try
        {
            LastRequestUrl = $"{GetApiBaseUrl()}lyric?id={songId}";
            using var response = await GetNeteaseResponseAsync(LastRequestUrl, cancellationToken);
            LastStatus = $"网易云歌词 HTTP {(int)response.StatusCode}";
            if (!response.IsSuccessStatusCode) return null;
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var result = FindLyricText(document.RootElement);
            LastLyricsLength = result?.Length ?? 0;
            return result;
        }
        catch (HttpRequestException ex) { LastStatus = "网易云歌词请求失败"; LastError = ex.Message; return null; }
        catch (TaskCanceledException) { return null; }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static string GetApiBaseUrl()
    {
        var configured = SettingsManager.Current.LyricsApiBaseUrl?.Trim();
        if (!string.IsNullOrWhiteSpace(configured)
            && Uri.TryCreate(configured, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return configured.TrimEnd('/') + "/";
        return DefaultApiBaseUrl;
    }

    private static LyricsTrack? ParseTrack(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var lines = new List<LyricsLine>();
        foreach (var line in raw.Replace("\r", string.Empty).Split('\n'))
        {
            var match = System.Text.RegularExpressions.Regex.Match(line, @"\[(\d{1,3}):(\d{2})(?:[\.:](\d{1,3}))?\]\s*(.*)$");
            if (!match.Success) continue;
            var seconds = (double)int.Parse(match.Groups[1].Value) * 60 + int.Parse(match.Groups[2].Value);
            var fraction = match.Groups[3].Success ? match.Groups[3].Value.PadRight(3, '0') : "0";
            seconds += int.Parse(fraction) / 1000.0;
            var text = match.Groups[4].Value.Trim();
            if (!string.IsNullOrWhiteSpace(text)) lines.Add(new LyricsLine(TimeSpan.FromSeconds(seconds), text));
        }
        if (lines.Count > 0)
        {
            var ordered = lines.OrderBy(x => x.Start).ToList();
            LastParsedLineCount = ordered.Count;
            LastParsedFirstLine = $"{ordered[0].Start} {ordered[0].Text}";
            return new LyricsTrack(ordered);
        }
        var plain = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        LastParsedLineCount = plain.Count;
        LastParsedFirstLine = plain.FirstOrDefault() ?? "";
        return plain.Count == 0 ? null : new LyricsTrack(plain.Select(x => new LyricsLine(TimeSpan.Zero, x)).ToList());
    }

    private static string? FindLyricText(JsonElement element)
    {
        var candidates = new List<string>();
        CollectLyricStrings(element, candidates);
        return candidates
            .OrderByDescending(x => System.Text.RegularExpressions.Regex.Matches(x, @"\[\d{1,3}:\d{2}(?:\.\d{1,3})?\]").Count)
            .ThenByDescending(x => x.Length)
            .FirstOrDefault();
    }

    private static void CollectLyricStrings(JsonElement element, List<string> candidates)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString();
            if (!string.IsNullOrWhiteSpace(value)) candidates.Add(value);
            return;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray()) CollectLyricStrings(child, candidates);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Contains("lyric", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("lrc", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("data", StringComparison.OrdinalIgnoreCase))
                CollectLyricStrings(property.Value, candidates);
        }
    }

    private static bool TryParseNetEaseSongId(string? value, out long songId) =>
        long.TryParse(value, out songId) && songId > 0;

    private static async Task<LrcLibResult?> GetAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<LrcLibResult>(cancellationToken: cancellationToken);
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
    }

    private static async Task<HttpResponseMessage> GetNeteaseResponseAsync(string url, CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, AppendTimestamp(url));
            var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode || attempt == maxAttempts - 1 || (status != 403 && status != 429 && status < 500))
                return response;

            response.Dispose();
            await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken);
        }

        throw new InvalidOperationException("Unreachable retry state.");
    }

    private static string AppendTimestamp(string url)
    {
        var separator = url.Contains('?') ? '&' : '?';
        return $"{url}{separator}timestamp={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
    }

    private static string StripTimestamps(string lyrics) => string.Join(Environment.NewLine,
        lyrics.Split('\n').Select(line =>
        {
            var end = line.IndexOf(']');
            return end >= 0 && line.StartsWith('[') ? line[(end + 1)..].TrimStart() : line;
        }).Where(line => !string.IsNullOrWhiteSpace(line)));

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // The provider now rejects the minimal desktop-client header with HTTP 403.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/128.0 Safari/537.36");
        client.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://music.163.com");
        client.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        return client;
    }

    private sealed class LrcLibResult
    {
        [JsonPropertyName("plainLyrics")] public string? PlainLyrics { get; set; }
        [JsonPropertyName("syncedLyrics")] public string? SyncedLyrics { get; set; }
    }

    public sealed record LyricsLine(TimeSpan Start, string Text);
    public sealed class LyricsTrack
    {
        public IReadOnlyList<LyricsLine> Lines { get; }
        public LyricsTrack(IReadOnlyList<LyricsLine> lines) => Lines = lines;
        public string? GetCurrentLine(TimeSpan position) => Lines.LastOrDefault(x => x.Start <= position)?.Text;
        public string ToLrc() => string.Join(Environment.NewLine, Lines.Select(line => $"[{(int)line.Start.TotalMinutes:00}:{line.Start.Seconds:00}.{line.Start.Milliseconds / 10:00}]{line.Text}"));
        public (string Current, string Next) GetCurrentAndNextLines(TimeSpan position)
        {
            if (Lines.Count == 0) return (string.Empty, string.Empty);
            var index = -1;
            for (var i = 0; i < Lines.Count; i++)
            {
                if (Lines[i].Start > position) break;
                index = i;
            }
            if (index < 0) return (Lines[0].Text, Lines.Count > 1 ? Lines[1].Text : string.Empty);
            return (Lines[index].Text, index + 1 < Lines.Count ? Lines[index + 1].Text : string.Empty);
        }
    }
}
