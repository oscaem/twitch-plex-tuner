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
        if (!File.Exists(_config.SubscriptionsPath))
        {
            Console.WriteLine($"Subscriptions file not found at {_config.SubscriptionsPath}");
            return;
        }

        var yaml = await File.ReadAllTextAsync(_config.SubscriptionsPath);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        var subs = deserializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(yaml);
        if (!subs.ContainsKey("subscriptions")) return;

        var logins = subs["subscriptions"].Keys.ToList();
        
        await EnsureAccessToken();

        // Get User IDs
        var usersResponse = await "https://api.twitch.tv/helix/users"
            .WithHeader("Client-ID", _config.ClientId)
            .WithOAuthBearerToken(_accessToken)
            .SetQueryParam("login", logins)
            .GetJsonAsync<TwitchResponse<TwitchUser>>();

        var newChannels = usersResponse.Data.Select(u => new ChannelInfo
        {
            Login = u.Login,
            DisplayName = u.DisplayName,
            UserId = u.Id,
            ProfileImageUrl = u.ProfileImageUrl
        }).ToList();

        // Get Stream Status
        var streamsResponse = await "https://api.twitch.tv/helix/streams"
            .WithHeader("Client-ID", _config.ClientId)
            .WithOAuthBearerToken(_accessToken)
            .SetQueryParam("user_id", newChannels.Select(c => c.UserId))
            .GetJsonAsync<TwitchResponse<TwitchStream>>();

        foreach (var channel in newChannels)
        {
            channel.CurrentStream = streamsResponse.Data.FirstOrDefault(s => s.UserId == channel.UserId);
        }

        _channels = newChannels;
    }

    public List<ChannelInfo> GetChannels() => _channels;
}
