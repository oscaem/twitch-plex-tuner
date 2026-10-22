using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using TwitchPlexTuner.Services;

namespace TwitchPlexTuner.Services;

public class TwitchUpdateService : BackgroundService
{
    private readonly TwitchService _twitchService;
    private readonly JellyfinService _jellyfinService;
    private readonly RecordingService _recordingService;
    private readonly ILogger<TwitchUpdateService> _logger;
    private readonly int _updateMinutes;

    public TwitchUpdateService(
        TwitchService twitchService,
        JellyfinService jellyfinService,
        RecordingService recordingService,
        ILogger<TwitchUpdateService> logger)
    {
        _twitchService = twitchService;
        _jellyfinService = jellyfinService;
        _recordingService = recordingService;
        _logger = logger;
        
        if (!int.TryParse(Environment.GetEnvironmentVariable("GUIDE_UPDATE_MINUTES"), out _updateMinutes))
        {
            _updateMinutes = 5;
        }
        else if (_updateMinutes < 2)
        {
            _logger.LogWarning("GUIDE_UPDATE_MINUTES={Minutes} is too aggressive; using 2 minutes to avoid hammering Twitch/Jellyfin.",
                _updateMinutes);
            _updateMinutes = 2;
        }
        
        _logger.LogInformation("Guide update interval: {Minutes} minutes", _updateMinutes);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogDebug("Updating Twitch channels...");
                await _twitchService.UpdateChannelsAsync();
                _logger.LogDebug("Twitch channel update complete; refreshing Jellyfin guide.");
                
                // Trigger Jellyfin guide refresh
                await _jellyfinService.RefreshGuideAsync(stoppingToken);
                
                // Notify recording service that fresh channel data is available
                _recordingService.NotifyChannelsUpdated();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating Twitch channels or refreshing Jellyfin guide");
            }

            await Task.Delay(TimeSpan.FromMinutes(_updateMinutes), stoppingToken);
        }
    }
}
