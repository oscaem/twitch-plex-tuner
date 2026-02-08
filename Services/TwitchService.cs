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

    public TwitchService(IOptions<TwitchConfig> config)
    {
        _config = config.Value;
    }

    public virtual async Task UpdateChannelsAsync()
    {
        try
        {
            Console.WriteLine($"=== LOADING CHANNELS FROM YAML ===");
            
            // 1. Load basic channel list from YAML
            if (!File.Exists(_config.SubscriptionsPath))
            {
                Console.WriteLine($"ERROR: Subscriptions file not found at {_config.SubscriptionsPath}");
                return;
            }

            var yaml = await File.ReadAllTextAsync(_config.SubscriptionsPath);
            var deserializer = new DeserializerBuilder().Build();
            var subs = deserializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(yaml);

            Dictionary<string, string>? channelDict = null;
            if (subs.ContainsKey("twitch_recorder")) channelDict = subs["twitch_recorder"];
            else if (subs.ContainsKey("subscriptions")) channelDict = subs["subscriptions"];

            if (channelDict == null || !channelDict.Any()) return;

            var newChannels = new List<ChannelInfo>();
            foreach (var kvp in channelDict)
            {
                var login = kvp.Value.Split('/').Last().ToLower();
                if (string.IsNullOrEmpty(login)) continue;

                newChannels.Add(new ChannelInfo
                {
                    Login = login,
                    DisplayName = kvp.Key,
                    ProfileImageUrl = $"https://static-cdn.jtvnw.net/jtv_user_pictures/{login}-profile_image-300x300.png"
                });
            }

            // 2. Fetch Live Data from Twitch API (if credentials exist)
            if (!string.IsNullOrEmpty(_config.ClientId) && !string.IsNullOrEmpty(_config.ClientSecret))
            {
                try 
                {
                    Console.WriteLine("Fetching live data from Twitch API...");
                    var tokenResp = await "https://id.twitch.tv/oauth2/token"
                        .PostUrlEncodedAsync(new {
                            client_id = _config.ClientId,
                            client_secret = _config.ClientSecret,
                            grant_type = "client_credentials"
                        })
                        .ReceiveJson<dynamic>();

                    string accessToken = tokenResp.access_token;
                    
                    // Split into chunks of 100 (API limit)
                    var chunks = newChannels.Select((x, i) => new { Index = i, Value = x })
                        .GroupBy(x => x.Index / 100)
                        .Select(x => x.Select(v => v.Value).ToList())
                        .ToList();

                    foreach (var chunk in chunks)
                    {
                        var userLogins = chunk.Select(c => c.Login).ToList();
                        
                        // Get Users (for real Profile Image)
                        var usersReq = "https://api.twitch.tv/helix/users"
                            .WithHeader("Client-ID", _config.ClientId)
                            .WithHeader("Authorization", $"Bearer {accessToken}");
                        
                        foreach(var login in userLogins) usersReq.SetQueryParam("login", login, true);
                        
                        var usersResp = await usersReq.GetJsonAsync<TwitchResponse<TwitchUser>>();
                        foreach(var user in usersResp.Data)
                        {
                            var ch = chunk.FirstOrDefault(c => c.Login.Equals(user.Login, StringComparison.OrdinalIgnoreCase));
                            if (ch != null) ch.ProfileImageUrl = user.ProfileImageUrl;
                        }

                        // Get Streams (for Live Status)
                        var streamsReq = "https://api.twitch.tv/helix/streams"
                            .WithHeader("Client-ID", _config.ClientId)
                            .WithHeader("Authorization", $"Bearer {accessToken}");
                        
                        foreach(var login in userLogins) streamsReq.SetQueryParam("user_login", login, true);

                        var streamsResp = await streamsReq.GetJsonAsync<TwitchResponse<TwitchStream>>();
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
            Console.WriteLine($"=== LOADED {_channels.Count} CHANNELS (API: {(!string.IsNullOrEmpty(_config.ClientId) ? "Yes" : "No")}) ===");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR in UpdateChannelsAsync: {ex.Message}");
        }
    }

    public virtual List<ChannelInfo> GetChannels() => _channels;
}
