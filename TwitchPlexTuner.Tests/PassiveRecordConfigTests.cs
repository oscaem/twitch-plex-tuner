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
                recording:
                  - livechannel
                recording_retention_days:
                  livechannel: 60
                passive_record:
                  - name: "Archive Streamer"
                    url: "https://www.twitch.tv/archivestreamer"
                    group: "Archive Group"
                    retention_days: 45
                """);
            var service = new TwitchService(Options.Create(new TwitchConfig { SubscriptionsPath = configPath }));

            await service.UpdateChannelsAsync();

            var passiveEntry = Assert.Single(service.GetPassiveRecordEntries());
            Assert.Equal("Archive Streamer", passiveEntry.Name);
            Assert.Equal("archivestreamer", passiveEntry.Login);
            Assert.Equal("Archive Group", passiveEntry.Group);
            Assert.Equal(45, passiveEntry.RetentionDays);
            Assert.DoesNotContain(service.GetChannels(), channel => channel.Login == passiveEntry.Login);
            var liveChannel = Assert.Single(service.GetChannels());
            Assert.Equal("livechannel", liveChannel.Login);
            Assert.True(liveChannel.RecordEnabled);
            Assert.Equal(60, liveChannel.RetentionDays);
        }
        finally
        {
            File.Delete(configPath);
        }
    }
}
