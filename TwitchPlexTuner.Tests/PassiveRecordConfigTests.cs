using Microsoft.Extensions.Options;
using TwitchPlexTuner.Models;
using TwitchPlexTuner.Services;

namespace TwitchPlexTuner.Tests;

public class PassiveRecordConfigTests
{
    [Fact]
    public async Task PassiveRecordEntries_AreLoadedWithoutAddingThemToTunerChannels()
    {
        var configPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(configPath, """
                subscriptions:
                  "Live Channel": "https://www.twitch.tv/livechannel"
                passive_record:
                  - name: "Archive Streamer"
                    url: "https://www.twitch.tv/archivestreamer"
                    group: "Archive Group"
                """);
            var service = new TwitchService(Options.Create(new TwitchConfig { SubscriptionsPath = configPath }));

            await service.UpdateChannelsAsync();

            var passiveEntry = Assert.Single(service.GetPassiveRecordEntries());
            Assert.Equal("Archive Streamer", passiveEntry.Name);
            Assert.Equal("archivestreamer", passiveEntry.Login);
            Assert.Equal("Archive Group", passiveEntry.Group);
            Assert.DoesNotContain(service.GetChannels(), channel => channel.Login == passiveEntry.Login);
            Assert.Contains(service.GetChannels(), channel => channel.Login == "livechannel");
        }
        finally
        {
            File.Delete(configPath);
        }
    }
}
