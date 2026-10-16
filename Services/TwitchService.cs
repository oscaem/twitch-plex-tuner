using Flurl.Http;
using Flurl;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using TwitchPlexTuner.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TwitchPlexTuner.Services;

public class TwitchService
{
    private readonly TwitchConfig _config;
    private List<ChannelInfo> _channels = new();
    private List<PassiveRecordEntry> _passiveRecordEntries = new();
    private readonly SemaphoreSlim _updateSemaphore = new(1, 1);
    private readonly SemaphoreSlim _tokenSemaphore = new(1, 1);
    private string? _accessToken;
    private DateTime _accessTokenExpiresAt = DateTime.MinValue;
    private DateTime _lastUpdatedUtc = DateTime.MinValue;

    public TwitchService(IOptions<TwitchConfig> config)
    {
        _config = config.Value;
    }

    public virtual async Task EnsureChannelsFreshAsync(TimeSpan maxAge)
    {
        if (DateTime.UtcNow - _lastUpdatedUtc < maxAge) return;

        await UpdateChannelsAsync();
    }

    public virtual async Task UpdateChannelsAsync()
    {
        await _updateSemaphore.WaitAsync();

        try
        {
            if (DateTime.UtcNow - _lastUpdatedUtc < TimeSpan.FromSeconds(15))
            {
                Console.WriteLine("Skipping Twitch update because a refresh just completed.");
                return;
            }

            Console.WriteLine($"=== LOADING CHANNELS FROM YAML ===");
            
            // 1. Load basic channel list from YAML
            if (!File.Exists(_config.SubscriptionsPath))
            {
                Console.WriteLine($"ERROR: Subscriptions file not found at {_config.SubscriptionsPath}");
                return;
            }

            var yaml = await File.ReadAllTextAsync(_config.SubscriptionsPath);
            var deserializer = new DeserializerBuilder().Build();
            
            // Parse as generic object to handle mixed types (dict + list)
            var root = deserializer.Deserialize<Dictionary<string, object>>(yaml);

            Dictionary<string, string>? channelDict = null;
            
            // Try twitch_recorder or subscriptions key
            foreach (var key in new[] { "twitch_recorder", "subscriptions" })
            {
                if (root.ContainsKey(key) && root[key] is Dictionary<object, object> raw)
                {
                    channelDict = raw.ToDictionary(k => k.Key.ToString()!, v => v.Value.ToString()!);
                    break;
                }
            }

            var passiveRecordEntries = ParsePassiveRecordEntries(root);
            _passiveRecordEntries = passiveRecordEntries;
            if ((channelDict == null || !channelDict.Any()) && !passiveRecordEntries.Any()) return;

            // Parse optional recording list
            var recordingLogins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (root.ContainsKey("recording") && root["recording"] is List<object> recordingList)
            {
                foreach (var item in recordingList)
                {
                    var login = item.ToString()?.Trim().ToLower();
                    if (!string.IsNullOrEmpty(login))
                        recordingLogins.Add(login);
                }
                Console.WriteLine($"Recording enabled for {recordingLogins.Count} channel(s): {string.Join(", ", recordingLogins)}");
            }

            var recordingRetentionDays = ParseRecordingRetentionDays(root);
            var newChannels = new List<ChannelInfo>();
            foreach (var kvp in channelDict ?? new Dictionary<string, string>())
            {
                var login = kvp.Value.Split('/').Last().ToLower();
                if (string.IsNullOrEmpty(login)) continue;
                int? channelRetentionDays = recordingRetentionDays.TryGetValue(login, out var retentionDays)
                    ? retentionDays
                    : null;

                newChannels.Add(new ChannelInfo
                {
                    Login = login,
                    DisplayName = kvp.Key,
                    ProfileImageUrl = $"https://static-cdn.jtvnw.net/jtv_user_pictures/{login}-profile_image-300x300.png",
                    RecordEnabled = recordingLogins.Contains(login),
                    RetentionDays = channelRetentionDays
                });
            }

            // 2. Fetch Live Data from Twitch API (if credentials exist)
            if (!string.IsNullOrEmpty(_config.ClientId) && !string.IsNullOrEmpty(_config.ClientSecret))
            {
                try 
                {
                    Console.WriteLine("Fetching live data from Twitch API...");
                    string? accessToken = await GetAccessTokenAsync();
                    if (string.IsNullOrEmpty(accessToken))
                    {
                        Console.WriteLine("Unable to fetch Twitch access token.");
                    }
                    
                    // Split into chunks of 100 (API limit)
                    var chunks = newChannels.Select((x, i) => new { Index = i, Value = x })
                        .GroupBy(x => x.Index / 100)
                        .Select(x => x.Select(v => v.Value).ToList())
                        .ToList();

                    foreach (var chunk in chunks)
                    {
                        if (string.IsNullOrEmpty(accessToken)) break;

                        var userLogins = chunk.Select(c => c.Login).ToList();
                        
                        // Get Users (for real Profile Image)
                        var usersReq = "https://api.twitch.tv/helix/users"
                            .WithHeader("Client-ID", _config.ClientId)
                            .WithHeader("Authorization", $"Bearer {accessToken}")
                            .SetQueryParam("login", userLogins);
                        
                        var usersResp = await usersReq.GetJsonAsync<TwitchResponse<TwitchUser>>();
                        foreach(var user in usersResp.Data)
                        {
                            var ch = chunk.FirstOrDefault(c => c.Login.Equals(user.Login, StringComparison.OrdinalIgnoreCase));
                            if (ch != null) ch.ProfileImageUrl = user.ProfileImageUrl;
                        }

                        // Get Streams (for Live Status)
                        var streamsReq = "https://api.twitch.tv/helix/streams"
                            .WithHeader("Client-ID", _config.ClientId)
                            .WithHeader("Authorization", $"Bearer {accessToken}")
                            .SetQueryParam("user_login", userLogins);

                        Console.WriteLine($"Requesting Streams: {streamsReq.Url}");

                        var streamsResp = await streamsReq.GetJsonAsync<TwitchResponse<TwitchStream>>();
                        
                        Console.WriteLine($"Received {streamsResp.Data.Count} streams from API for chunk of {chunk.Count}");

                        foreach(var stream in streamsResp.Data)
                        {
                            var ch = chunk.FirstOrDefault(c => c.Login.Equals(stream.UserLogin, StringComparison.OrdinalIgnoreCase));
                            if (ch != null)
                            {
                                ch.IsLive = true;
                                ch.StreamTitle = stream.Title;
                                ch.GameName = stream.GameName;
                                ch.StartedAt = stream.StartedAt;
                                ch.StreamThumbnailUrl = stream.ThumbnailUrl.Replace("{width}", "640").Replace("{height}", "360");
                                Console.WriteLine($"Marking {ch.Login} as LIVE: {ch.StreamTitle}");
                            }
                            else
                            {
                                Console.WriteLine($"Stream found for {stream.UserLogin} but no matching channel in chunk");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"API Error: {ex.Message}");
                }
            }

            _channels = newChannels;
            _lastUpdatedUtc = DateTime.UtcNow;
            Console.WriteLine($"=== LOADED {_channels.Count} CHANNELS (API: {(!string.IsNullOrEmpty(_config.ClientId) ? "Yes" : "No")}) ===");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR in UpdateChannelsAsync: {ex.Message}");
        }
        finally
        {
            _updateSemaphore.Release();
        }
    }

    public virtual List<ChannelInfo> GetChannels() => _channels;

    public virtual List<PassiveRecordEntry> GetPassiveRecordEntries() => _passiveRecordEntries;

    public async Task<List<TwitchVideo>> GetAvailableVideosAsync(string login, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_config.ClientId) || string.IsNullOrWhiteSpace(_config.ClientSecret))
            throw new InvalidOperationException("Twitch API credentials are required for passive recording.");

        var accessToken = await GetAccessTokenAsync();
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("Unable to obtain a Twitch API access token.");

        var users = await "https://api.twitch.tv/helix/users"
            .WithHeader("Client-ID", _config.ClientId)
            .WithHeader("Authorization", $"Bearer {accessToken}")
            .SetQueryParam("login", login)
            .GetJsonAsync<TwitchResponse<TwitchUser>>(cancellationToken: ct);
        var userId = users.Data.FirstOrDefault()?.Id;
        if (string.IsNullOrEmpty(userId))
            throw new InvalidOperationException($"Twitch user '{login}' was not found.");

        var videos = new List<TwitchVideo>();
        string? cursor = null;
        do
        {
            var request = "https://api.twitch.tv/helix/videos"
                .WithHeader("Client-ID", _config.ClientId)
                .WithHeader("Authorization", $"Bearer {accessToken}")
                .SetQueryParam("user_id", userId)
                .SetQueryParam("type", "archive")
                .SetQueryParam("period", "all")
                .SetQueryParam("first", 100);
            if (!string.IsNullOrEmpty(cursor))
                request = request.SetQueryParam("after", cursor);

            var page = await request.GetJsonAsync<TwitchVideosResponse>(cancellationToken: ct);
            videos.AddRange(page.Data);
            cursor = page.Pagination.Cursor;
        }
        while (!string.IsNullOrEmpty(cursor));

        return videos;
    }

    private static List<PassiveRecordEntry> ParsePassiveRecordEntries(Dictionary<string, object> root)
    {
        var entries = new List<PassiveRecordEntry>();
        if (!root.TryGetValue("passive_record", out var section) || section is not List<object> rawEntries)
            return entries;

        foreach (var rawEntry in rawEntries)
        {
            if (rawEntry is not Dictionary<object, object> fields)
                continue;

            string GetField(string name) =>
                fields.FirstOrDefault(field =>
                    string.Equals(field.Key.ToString(), name, StringComparison.OrdinalIgnoreCase))
                    .Value?.ToString()?.Trim() ?? string.Empty;

            var name = GetField("name");
            var url = GetField("url");
            if (string.IsNullOrWhiteSpace(name) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                !uri.Host.Equals("twitch.tv", StringComparison.OrdinalIgnoreCase) &&
                !uri.Host.Equals("www.twitch.tv", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Skipping invalid passive_record entry (name: '{name}', url: '{url}'). A name and Twitch channel URL are required.");
                continue;
            }

            var login = uri.AbsolutePath.Trim('/').Split('/').LastOrDefault()?.ToLowerInvariant() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(login))
            {
                Console.WriteLine($"Skipping passive_record entry '{name}': Twitch channel URL has no channel name.");
                continue;
            }

            var group = GetField("group");
            entries.Add(new PassiveRecordEntry
            {
                Name = name,
                Url = url,
                Login = login,
                Group = string.IsNullOrWhiteSpace(group) ? null : group,
                RetentionDays = ParseRetentionDays(GetField("retention_days"), $"passive_record entry '{name}'")
            });
        }

        return entries;
    }

    private static Dictionary<string, int> ParseRecordingRetentionDays(Dictionary<string, object> root)
    {
        var retentionDays = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetValue("recording_retention_days", out var section) ||
            section is not Dictionary<object, object> rawOverrides)
            return retentionDays;

        foreach (var item in rawOverrides)
        {
            var login = item.Key.ToString()?.Trim().ToLowerInvariant();
            var days = ParseRetentionDays(item.Value?.ToString(), $"recording_retention_days.{login}");
            if (!string.IsNullOrWhiteSpace(login) && days.HasValue)
                retentionDays[login] = days.Value;
        }

        return retentionDays;
    }

    private static int? ParseRetentionDays(string? value, string settingName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (int.TryParse(value, out var days) && days >= 0)
            return days;

        Console.WriteLine($"Ignoring invalid {settingName}: expected a non-negative whole number of days.");
        return null;
    }

    private async Task<string?> GetAccessTokenAsync()
    {
        if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _accessTokenExpiresAt)
        {
            return _accessToken;
        }

        await _tokenSemaphore.WaitAsync();
        try
        {
            if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _accessTokenExpiresAt)
            {
                return _accessToken;
            }

            var tokenResp = await "https://id.twitch.tv/oauth2/token"
                .PostUrlEncodedAsync(new {
                    client_id = _config.ClientId,
                    client_secret = _config.ClientSecret,
                    grant_type = "client_credentials"
                })
                .ReceiveJson<TwitchTokenResponse>();

            _accessToken = tokenResp.AccessToken;
            _accessTokenExpiresAt = DateTime.UtcNow.AddSeconds(Math.Max(60, tokenResp.ExpiresIn - 300));
            return _accessToken;
        }
        finally
        {
            _tokenSemaphore.Release();
        }
    }
}
