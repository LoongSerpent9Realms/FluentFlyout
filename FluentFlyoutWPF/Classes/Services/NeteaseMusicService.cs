// Copyright (c) 2024-2026 The PulseFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net.Http.Json;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.IO;
using System.Text.Json.Serialization;

namespace FluentFlyoutWPF.Classes.Services;

/// <summary>Small, local-first client for the Netease Music API used by the media flyout.</summary>
public static class NeteaseMusicService
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private const string ApiRoot = "https://music.loongst.com";
    private static readonly HttpClient Client = CreateClient();
    private static readonly string CookiePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PulseFlyout", "netease.cookie");
    private static readonly string LikeListPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PulseFlyout", "netease.likelist");
    private static string? _cookie;
    private static LikeListCache? _likeList;

    public static bool IsLoggedIn => !string.IsNullOrWhiteSpace(GetCookie());

    public static async Task<NeteaseAccount?> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        if (!IsLoggedIn) return null;
        var response = await GetJsonAsync<AccountResponse>("/user/account", cancellationToken, addTimestamp: true, authenticated: true);
        var id = response?.Account?.Id ?? response?.Profile?.UserId;
        var name = response?.Profile?.Nickname ?? response?.Account?.UserName;
        return id is > 0 || !string.IsNullOrWhiteSpace(name) ? new NeteaseAccount(id, name) : null;
    }

    /// <summary>Fetches and locally caches the authenticated user's liked song IDs.</summary>
    public static async Task<IReadOnlySet<long>> GetLikedSongIdsAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (!IsLoggedIn) return (IReadOnlySet<long>)new HashSet<long>();
        if (!refresh && TryLoadLikeList(out var cached) && cached is not null) return cached.Ids.ToHashSet();

        var account = await GetJsonAsync<AccountResponse>("/user/account", cancellationToken, addTimestamp: true, authenticated: true);
        var uid = account?.Account?.Id ?? account?.Profile?.UserId;
        if (uid is null || uid <= 0) return new HashSet<long>();

        var response = await GetJsonAsync<LikeListResponse>($"/likelist?uid={uid.Value}", cancellationToken, addTimestamp: true, authenticated: true);
        var ids = response?.Ids?.Where(id => id > 0).ToHashSet() ?? new HashSet<long>();
        SaveLikeList(new LikeListCache(uid.Value, ids.ToArray()));
        return ids;
    }

    /// <summary>Refreshes the local liked-song cache after QR login.</summary>
    public static async Task WarmLikeListAsync(CancellationToken cancellationToken = default) =>
        _ = await GetLikedSongIdsAsync(refresh: true, cancellationToken);

    public static async Task<bool> IsSongLikedAsync(long songId, CancellationToken cancellationToken = default, bool refresh = false)
    {
        if (songId <= 0 || !IsLoggedIn) return false;
        var ids = await GetLikedSongIdsAsync(refresh, cancellationToken);
        return ids.Contains(songId);
    }

    public static async Task<NeteaseQrCode?> CreateQrCodeAsync(CancellationToken cancellationToken = default)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var key = await GetJsonAsync<QrKeyResponse>($"/login/qr/key?timestamp={timestamp}", cancellationToken);
        var unikey = key?.Data?.Unikey;
        if (string.IsNullOrWhiteSpace(unikey)) return null;

        var qr = await GetJsonAsync<QrCreateResponse>(
            $"/login/qr/create?key={Uri.EscapeDataString(unikey)}&platform=web&qrimg=true&timestamp={timestamp}&ua=pc",
            cancellationToken);
        var qrimg = qr?.Data?.Qrimg;
        var qrurl = qr?.Data?.Qrurl;
        if (string.IsNullOrWhiteSpace(qrimg) && string.IsNullOrWhiteSpace(qrurl)) return null;
        return new NeteaseQrCode(unikey, qrimg, qrurl);
    }

    public static async Task<NeteaseQrLoginResult> CheckQrCodeAsync(string key, CancellationToken cancellationToken = default)
    {
        var result = await GetJsonAsync<QrCheckResponse>(
            $"/login/qr/check?key={Uri.EscapeDataString(key)}&timestamp={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}&ua=pc",
            cancellationToken);
        var status = result?.Code switch
        {
            803 => NeteaseQrLoginStatus.Authorized,
            802 => NeteaseQrLoginStatus.WaitingForConfirmation,
            801 => NeteaseQrLoginStatus.WaitingForScan,
            800 => NeteaseQrLoginStatus.Expired,
            _ => NeteaseQrLoginStatus.Unknown
        };
        if (status == NeteaseQrLoginStatus.Authorized && !string.IsNullOrWhiteSpace(result?.Cookie))
            SaveCookie(result.Cookie);
        return new NeteaseQrLoginResult(status);
    }

    public static async Task<long?> ResolveSongIdAsync(string title, string artist, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var keywords = Uri.EscapeDataString($"{title} {artist}".Trim());
        var result = await GetJsonAsync<SearchResponse>($"/search?keywords={keywords}&limit=10", cancellationToken);
        var songs = result?.Result?.Songs;
        if (songs is null || songs.Count == 0) return null;
        var selected = songs
            .Select((song, index) => new { Song = song, Index = index, Score = ScoreSong(song, title, artist) })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Index)
            .FirstOrDefault(x => x.Score >= 200)?.Song;
        if (selected is null) return null;
        Logger.Debug("Netease search selected: {0} ({1}) for '{2}' / '{3}'", selected.Name, selected.Id, title, artist);
        return selected.Id is > 0 ? selected.Id.Value : null;
    }

    private static int ScoreSong(Song song, string title, string artist)
    {
        var name = song.Name ?? string.Empty;
        var normalizedName = Normalize(name);
        var normalizedTitle = Normalize(title);
        var score = normalizedName == normalizedTitle ? 200 : 0;
        if (score == 0 && StripTitleQualifier(normalizedName) == StripTitleQualifier(normalizedTitle)) score = 160;
        if (score == 0 && (normalizedName.Contains(normalizedTitle, StringComparison.OrdinalIgnoreCase)
            || normalizedTitle.Contains(normalizedName, StringComparison.OrdinalIgnoreCase))) score = 80;

        var requestedArtists = SplitArtists(artist);
        var songArtists = (song.Artists ?? []).SelectMany(x => SplitArtists(x.Name ?? string.Empty)).ToArray();
        if (requestedArtists.Any(requested => songArtists.Any(candidate => candidate == requested
            || candidate.Contains(requested, StringComparison.OrdinalIgnoreCase)
            || requested.Contains(candidate, StringComparison.OrdinalIgnoreCase)))) score += 50;
        if (name.Contains("伴奏", StringComparison.OrdinalIgnoreCase) || name.Contains("纯音乐", StringComparison.OrdinalIgnoreCase)
            || name.Contains("instrumental", StringComparison.OrdinalIgnoreCase)) score -= 100;
        return score;
    }

    private static string Normalize(string value) =>
        value.Trim().Replace("（", "(").Replace("）", ")").ToLowerInvariant();

    private static string StripTitleQualifier(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, @"\s*\([^)]*\)", string.Empty).Trim();

    private static IEnumerable<string> SplitArtists(string value) =>
        System.Text.RegularExpressions.Regex.Split(Normalize(value), @"[\/／、,&，;；]+")
            .Select(x => x.Trim()).Where(x => x.Length > 0);

    public static async Task<bool> LikeAsync(long songId, bool like = true, CancellationToken cancellationToken = default)
    {
        if (songId <= 0 || !IsLoggedIn) return false;
        var result = await GetJsonAsync<CodeResponse>(
            $"/like?id={songId}&like={like.ToString().ToLowerInvariant()}", cancellationToken, addTimestamp: true, authenticated: true);
        var success = result?.Code == 200;
        if (success)
        {
            if (!TryLoadLikeList(out var cache) || cache is null)
                await GetLikedSongIdsAsync(refresh: true, cancellationToken);
            if (TryLoadLikeList(out cache) && cache is not null)
            {
                var ids = like
                    ? (cache.Ids.Contains(songId) ? cache.Ids : cache.Ids.Append(songId).ToArray())
                    : cache.Ids.Where(id => id != songId).ToArray();
                SaveLikeList(cache with { Ids = ids });
            }
        }
        return success;
    }

    public static void ClearLogin()
    {
        _cookie = null;
        _likeList = null;
        try { if (File.Exists(CookiePath)) File.Delete(CookiePath); } catch { }
        try { if (File.Exists(LikeListPath)) File.Delete(LikeListPath); } catch { }
    }

    public static void SaveLoginCookie(string cookie)
    {
        if (!string.IsNullOrWhiteSpace(cookie)) SaveCookie(cookie);
    }

    private static bool TryLoadLikeList(out LikeListCache? cache)
    {
        if (_likeList is not null) { cache = _likeList; return true; }
        try
        {
            if (!File.Exists(LikeListPath)) { cache = null; return false; }
            var protectedBytes = File.ReadAllBytes(LikeListPath);
            var json = System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser));
            cache = JsonSerializer.Deserialize<LikeListCache>(json);
            _likeList = cache;
            return cache is not null;
        }
        catch { cache = null; return false; }
    }

    private static void SaveLikeList(LikeListCache cache)
    {
        _likeList = cache;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LikeListPath)!);
            var json = JsonSerializer.Serialize(cache);
            var protectedBytes = ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(LikeListPath, protectedBytes);
        }
        catch { }
    }

    private static string? GetCookie()
    {
        if (_cookie is not null) return _cookie;
        try
        {
            if (!File.Exists(CookiePath)) return null;
            var protectedBytes = File.ReadAllBytes(CookiePath);
            _cookie = System.Text.Encoding.UTF8.GetString(
                ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser));
        }
        catch { _cookie = null; }
        return _cookie;
    }

    private static void SaveCookie(string cookie)
    {
        _cookie = cookie;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CookiePath)!);
            var protectedBytes = ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(cookie), optionalEntropy: null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(CookiePath, protectedBytes);
        }
        catch { }
    }

    private static async Task<T?> GetJsonAsync<T>(string path, CancellationToken cancellationToken, bool addTimestamp = false, bool authenticated = false)
    {
        var separator = path.Contains('?') ? '&' : '?';
        // The API sample uses timerstamp; the proxy cache distinguishes this parameter.
        var url = ApiRoot + path + (addTimestamp ? $"{separator}timerstamp={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}" : "");
        var cookie = authenticated ? GetCookie() : null;
        if (!string.IsNullOrWhiteSpace(cookie))
            url += $"&cookie={Uri.EscapeDataString(cookie)}";
        try
        {
            return await Client.GetFromJsonAsync<T>(url, cancellationToken);
        }
        catch (HttpRequestException) { return default; }
        catch (TaskCanceledException) { return default; }
        catch (JsonException ex)
        {
            Logger.Debug(ex, "Netease API JSON parse failed: {0}", path);
            return default;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/128.0 Safari/537.36");
        client.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://music.163.com");
        return client;
    }

    public sealed record NeteaseQrCode(string Key, string? Base64Image, string? QrUrl);
    public sealed record NeteaseAccount(int? Id, string? Name);
    public sealed record NeteaseQrLoginResult(NeteaseQrLoginStatus Status);
    public enum NeteaseQrLoginStatus { Unknown, WaitingForScan, WaitingForConfirmation, Authorized, Expired }

    private sealed class QrKeyResponse { public QrKeyData? Data { get; set; } }
    private sealed class QrCreateResponse { public QrCreateData? Data { get; set; } }
    private sealed class QrCheckResponse { public int Code { get; set; } public string? Cookie { get; set; } }
    private sealed class QrKeyData { public string? Unikey { get; set; } }
    private sealed class QrCreateData { public string? Qrimg { get; set; } public string? Qrurl { get; set; } }
    private sealed class AccountResponse { public AccountData? Account { get; set; } public ProfileData? Profile { get; set; } }
    private sealed class AccountData { public int Id { get; set; } public string? UserName { get; set; } }
    private sealed class ProfileData { public int UserId { get; set; } public string? Nickname { get; set; } }
    private sealed class LikeListResponse { public List<long>? Ids { get; set; } }
    private sealed record LikeListCache(int Uid, long[] Ids);
    private sealed class CodeResponse { public int Code { get; set; } }
    private sealed class SearchResponse { public SearchResult? Result { get; set; } }
    private sealed class SearchResult { public List<Song>? Songs { get; set; } }
    private sealed class Song
    {
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long? Id { get; set; }
        public string? Name { get; set; }
        public List<SongArtist>? Artists { get; set; }
    }
    private sealed class SongArtist { public string? Name { get; set; } }
}
