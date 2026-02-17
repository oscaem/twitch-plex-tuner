using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TwitchPlexTuner.Models;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;

namespace TwitchPlexTuner.Services;

public class JellyfinService : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TwitchConfig _config;
    private readonly ILogger<JellyfinService> _logger;
    private string? _cachedRefreshTaskId;

    public JellyfinService(IHttpClientFactory httpClientFactory, IOptions<TwitchConfig> config, ILogger<JellyfinService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(_config.JellyfinUrl) || string.IsNullOrEmpty(_config.JellyfinApiKey))
        {
            _logger.LogInformation("Jellyfin URL or API Key not configured. Jellyfin guide refresh disabled.");
            return;
        }

        _logger.LogInformation("Jellyfin guide refresh service enabled for {Url}", _config.JellyfinUrl);

        // This service mainly provides RefreshGuideAsync to be called by others,
        // but it could also do an initial refresh.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        await RefreshGuideAsync(stoppingToken);
    }

    public async Task RefreshGuideAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_config.JellyfinUrl) || string.IsNullOrEmpty(_config.JellyfinApiKey)) return;

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Emby-Token", _config.JellyfinApiKey);

            // 1. Find the task ID if not cached
            if (string.IsNullOrEmpty(_cachedRefreshTaskId))
            {
                var tasksUrl = $"{_config.JellyfinUrl.TrimEnd('/')}/ScheduledTasks";
                var response = await client.GetAsync(tasksUrl, ct);
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(content);
                    var refreshTask = doc.RootElement.EnumerateArray()
                        .FirstOrDefault(t => t.GetProperty("Key").GetString() == "RefreshGuide" || 
                                             t.GetProperty("Name").GetString()?.Contains("Refresh Guide", StringComparison.OrdinalIgnoreCase) == true);

                    if (refreshTask.ValueKind != JsonValueKind.Undefined)
                    {
                        _cachedRefreshTaskId = refreshTask.GetProperty("Id").GetString();
                        _logger.LogInformation("Found Jellyfin Refresh Guide task ID: {Id}", _cachedRefreshTaskId);
                    }
                }
            }

            // 2. Trigger the task
            if (!string.IsNullOrEmpty(_cachedRefreshTaskId))
            {
                var runUrl = $"{_config.JellyfinUrl.TrimEnd('/')}/ScheduledTasks/Running/{_cachedRefreshTaskId}";
                var runResponse = await client.PostAsync(runUrl, null, ct);
                
                if (runResponse.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Successfully triggered Jellyfin guide refresh.");
                }
                else if (runResponse.StatusCode == System.Net.HttpStatusCode.NoContent)
                {
                    _logger.LogInformation("Jellyfin guide refresh triggered (NoContent).");
                }
                else
                {
                    _logger.LogWarning("Failed to trigger Jellyfin guide refresh. Status: {Status}", runResponse.StatusCode);
                }
            }
            else
            {
                _logger.LogWarning("Could not find 'Refresh Guide' task in Jellyfin.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing Jellyfin guide");
        }
    }
}
