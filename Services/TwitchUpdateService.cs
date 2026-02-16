using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using TwitchPlexTuner.Services;

namespace TwitchPlexTuner.Services;

public class TwitchUpdateService : BackgroundService
{
    private readonly TwitchService _twitchService;
    private readonly ILogger<TwitchUpdateService> _logger;
    private readonly int _updateMinutes;

    public TwitchUpdateService(TwitchService twitchService, ILogger<TwitchUpdateService> logger)
    {
        _twitchService = twitchService;
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
                _logger.LogInformation("Update complete.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating Twitch channels");
            }

            await Task.Delay(TimeSpan.FromMinutes(_updateMinutes), stoppingToken);
        }
    }
}
