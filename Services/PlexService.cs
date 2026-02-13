using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TwitchPlexTuner.Models;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace TwitchPlexTuner.Services;

public class PlexService : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TwitchConfig _config;
    private readonly ILogger<PlexService> _logger;

    public PlexService(IHttpClientFactory httpClientFactory, IOptions<TwitchConfig> config, ILogger<PlexService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay to let application startup
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (string.IsNullOrEmpty(_config.PlexServerUrl) || string.IsNullOrEmpty(_config.PlexToken))
                {
                    _logger.LogWarning("Plex Server URL or Token not configured. Skipping Guide Refresh.");
                }
                else
                {
                    await RefreshPlexGuideAsync(stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing Plex guide");
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    private async Task RefreshPlexGuideAsync(CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        var devicesUrl = $"{_config.PlexServerUrl.TrimEnd('/')}/livetv/dvrs?X-Plex-Token={_config.PlexToken}";

        try
        {
            var response = await client.GetAsync(devicesUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError($"Failed to fetch DVRs. Status: {response.StatusCode}");
                return;
            }

            var xmlContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = System.Xml.Linq.XDocument.Parse(xmlContent);
            var dvrs = doc.Descendants("Dvr");

            foreach (var dvr in dvrs)
            {
                var key = dvr.Attribute("key")?.Value;
                if (string.IsNullOrEmpty(key)) continue;

                // 1. Force Threadfin to update its XEPG (Guide & Playlist) - OPTIONAL
                if (!string.IsNullOrEmpty(_config.ThreadfinUrl))
                {
                    try
                    {
                        await TriggerThreadfinUpdateAsync(client, cancellationToken);
                        // Wait a moment for Threadfin to process
                        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                    }
                    catch (Exception ex)
                    {
                         _logger.LogWarning(ex, "Failed to update Threadfin, continuing...");
                    }
                }

                // 2. Trigger Plex Refresh
                var refreshUrl = $"{_config.PlexServerUrl.TrimEnd('/')}/livetv/dvrs/{key}/refreshGuide?X-Plex-Token={_config.PlexToken}";
                _logger.LogInformation($"Triggering Guide Refresh for DVR {key}...");
                
                var refreshResponse = await client.PostAsync(refreshUrl, null, cancellationToken);
                if (refreshResponse.IsSuccessStatusCode)
                {
                    _logger.LogInformation($"Successfully refreshed guide for DVR {key}.");
                }
                else
                {
                    _logger.LogError($"Failed to refresh guide for DVR {key}. Status: {refreshResponse.StatusCode}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Plex DVR response or refresh guide.");
        }
    }

    private async Task TriggerThreadfinUpdateAsync(HttpClient client, CancellationToken ct)
    {
        try
        {
            var url = $"{_config.ThreadfinUrl.TrimEnd('/')}/api/";
            _logger.LogInformation("Triggering Threadfin XEPG update at {Url}...", url);
            
            // Threadfin expects: {"cmd":"update.xepg"}
            var json = JsonSerializer.Serialize(new { cmd = "update.xepg" });
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await client.PostAsync(url, content, ct);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Threadfin update triggered successfully.");
            }
            else
            {
                _logger.LogWarning("Threadfin update failed. Status: {Status}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error triggering Threadfin update");
        }
    }
}
