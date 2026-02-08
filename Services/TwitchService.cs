using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Flurl.Http;
using Microsoft.Extensions.Options;
using TwitchPlexTuner.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TwitchPlexTuner.Services;

public class TwitchService
{
    private readonly TwitchConfig _config;
    private string _accessToken = string.Empty;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private List<ChannelInfo> _channels = new();

    public TwitchService(IOptions<TwitchConfig> config)
    {
        _config = config.Value;
    }

    private async Task EnsureAccessToken()
    {
        if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenExpiry) return;

        var response = await "https://id.twitch.tv/oauth2/token"
            .PostUrlEncodedAsync(new
            {
                client_id = _config.ClientId,
                client_secret = _config.ClientSecret,
                grant_type = "client_credentials"
            })
            .ReceiveJson<dynamic>();

        _accessToken = response.access_token;
        int expiresIn = (int)response.expires_in;
        _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60);
    }

    public async Task UpdateChannelsAsync()
    {
        try
        {
            Console.WriteLine($"=== UPDATE CHANNELS START ===");
            Console.WriteLine($"ClientId: {(_config.ClientId?.Length > 0 ? "SET" : "MISSING")}");
            Console.WriteLine($"ClientSecret: {(_config.ClientSecret?.Length > 0 ? "SET" : "MISSING")}");
            Console.WriteLine($"SubscriptionsPath: {_config.SubscriptionsPath}");

            if (!File.Exists(_config.SubscriptionsPath))
            {
                Console.WriteLine($"ERROR: Subscriptions file not found at {_config.SubscriptionsPath}");
                return;
            }

            Console.WriteLine("Reading YAML file...");
            var yaml = await File.ReadAllTextAsync(_config.SubscriptionsPath);
            Console.WriteLine($"YAML content length: {yaml.Length}");

            var deserializer = new DeserializerBuilder().Build();
            var subs = deserializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(yaml);
            Console.WriteLine($"YAML keys: {string.Join(", ", subs.Keys)}");

            // Support both "twitch_recorder" and "subscriptions" keys
            Dictionary<string, string>? channelDict = null;
            if (subs.ContainsKey("twitch_recorder"))
            {
                channelDict = subs["twitch_recorder"];
                Console.WriteLine($"Using 'twitch_recorder' key with {channelDict.Count} entries");
            }
            else if (subs.ContainsKey("subscriptions"))
            {
                channelDict = subs["subscriptions"];
                Console.WriteLine($"Using 'subscriptions' key with {channelDict.Count} entries");
            }

            if (channelDict == null || !channelDict.Any())
            {
                Console.WriteLine("ERROR: No channels found in subscriptions file");
                return;
            }

            // Extract login names from Twitch URLs
            var logins = channelDict.Values
                .Select(url => url.Split('/').Last().ToLower())
                .Where(login => !string.IsNullOrEmpty(login))
                .ToList();

            Console.WriteLine($"Found {logins.Count} channels: {string.Join(", ", logins)}");

            Console.WriteLine("Getting access token...");
            await EnsureAccessToken();
            Console.WriteLine("Access token obtained");

            // Get User IDs
            Console.WriteLine("Fetching user data from Twitch API...");
            var usersResponse = await "https://api.twitch.tv/helix/users"
                .WithHeader("Client-ID", _config.ClientId)
                .WithOAuthBearerToken(_accessToken)
                .SetQueryParam("login", logins)
                .GetJsonAsync<TwitchResponse<TwitchUser>>();

            Console.WriteLine($"Got {usersResponse.Data.Count} users from Twitch");

            var newChannels = usersResponse.Data.Select(u => new ChannelInfo
            {
                Login = u.Login,
                DisplayName = u.DisplayName,
                UserId = u.Id,
                ProfileImageUrl = u.ProfileImageUrl
            }).ToList();

            // Get Stream Status
            Console.WriteLine("Fetching stream status...");
            var streamsResponse = await "https://api.twitch.tv/helix/streams"
                .WithHeader("Client-ID", _config.ClientId)
                .WithOAuthBearerToken(_accessToken)
                .SetQueryParam("user_id", newChannels.Select(c => c.UserId))
                .GetJsonAsync<TwitchResponse<TwitchStream>>();

            Console.WriteLine($"Got status for {streamsResponse.Data.Count} live streams");

            foreach (var channel in newChannels)
            {
                channel.CurrentStream = streamsResponse.Data.FirstOrDefault(s => s.UserId == channel.UserId);
            }

            _channels = newChannels;
            Console.WriteLine($"=== UPDATE COMPLETE: {_channels.Count} channels loaded ===");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR in UpdateChannelsAsync: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }
    }

    public List<ChannelInfo> GetChannels() => _channels;
}
