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

    public async Task UpdateChannelsAsync()
    {
        try
        {
            Console.WriteLine($"=== LOADING CHANNELS FROM YAML ===");
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

            // Extract login names from Twitch URLs and create simple channel info
            var newChannels = new List<ChannelInfo>();
            foreach (var kvp in channelDict)
            {
                var displayName = kvp.Key;
                var url = kvp.Value;
                var login = url.Split('/').Last().ToLower();

                if (string.IsNullOrEmpty(login))
                {
                    Console.WriteLine($"WARNING: Skipping invalid URL: {url}");
                    continue;
                }

                newChannels.Add(new ChannelInfo
                {
                    Login = login,
                    DisplayName = displayName,
                    // We'll use a default profile image URL structure
                    ProfileImageUrl = $"https://static-cdn.jtvnw.net/jtv_user_pictures/{login}-profile_image-300x300.png"
                });
            }

            _channels = newChannels;
            Console.WriteLine($"=== LOADED {_channels.Count} CHANNELS ===");
            Console.WriteLine($"Channels: {string.Join(", ", _channels.Select(c => c.Login))}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR in UpdateChannelsAsync: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }
    }

    public List<ChannelInfo> GetChannels() => _channels;
}
