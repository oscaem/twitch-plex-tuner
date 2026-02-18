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
        
        if (!int.TryParse(Environment.GetEnvironmentVariable("GUIDE_UPDATE_MINUTES"), out _updateMinutes) || _updateMinutes < 1)
        {
            _updateMinutes = 5; // Default to 5 minutes
        }
        
        _logger.LogInformation("Guide update interval: {Minutes} minutes", _updateMinutes);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Updating Twitch channels...");
                await _twitchService.UpdateChannelsAsync();
                _logger.LogInformation("Update complete. Triggering Jellyfin guide refresh...");
                
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
