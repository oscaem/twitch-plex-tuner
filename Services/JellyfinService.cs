using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TwitchPlexTuner.Models;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;

namespace TwitchPlexTuner.Services;

/// <summary>
/// Service that triggers Jellyfin guide refreshes on demand.
/// Called by TwitchUpdateService after channel state is updated.
/// </summary>
public class JellyfinService
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

        if (!string.IsNullOrEmpty(_config.JellyfinUrl) && !string.IsNullOrEmpty(_config.JellyfinApiKey))
        {
            _logger.LogInformation("Jellyfin guide refresh enabled for {Url}", _config.JellyfinUrl);
        }
        else
        {
            _logger.LogWarning("Jellyfin URL or API Key not configured. Jellyfin guide refresh disabled.");
        }
    }

    public async Task RefreshGuideAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_config.JellyfinUrl) || string.IsNullOrEmpty(_config.JellyfinApiKey))
        {
            _logger.LogWarning("Skipping Jellyfin refresh: URL or API Key is empty. JELLYFIN_URL={Url}, JELLYFIN_API_KEY={HasKey}",
                _config.JellyfinUrl ?? "(null)",
                string.IsNullOrEmpty(_config.JellyfinApiKey) ? "(empty)" : "(set)");
            return;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Emby-Token", _config.JellyfinApiKey);

            // 1. Find the task ID if not cached
            if (string.IsNullOrEmpty(_cachedRefreshTaskId))
            {
                var tasksUrl = $"{_config.JellyfinUrl.TrimEnd('/')}/ScheduledTasks";
                _logger.LogInformation("Fetching Jellyfin scheduled tasks from {Url}", tasksUrl);
                var response = await client.GetAsync(tasksUrl, ct);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Failed to fetch Jellyfin scheduled tasks. Status: {Status}", response.StatusCode);
                    return;
                }

                var content = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(content);
                
                // Log all available tasks for debugging
                var taskNames = doc.RootElement.EnumerateArray()
                    .Select(t => $"{t.GetProperty("Key").GetString()} ({t.GetProperty("Name").GetString()})")
                    .ToList();
                _logger.LogInformation("Available Jellyfin tasks: {Tasks}", string.Join(", ", taskNames));

                // Search broadly for the guide refresh task
                var refreshTask = doc.RootElement.EnumerateArray()
                    .FirstOrDefault(t =>
                    {
                        var key = t.GetProperty("Key").GetString() ?? "";
                        var name = t.GetProperty("Name").GetString() ?? "";
                        return key.Contains("RefreshGuide", StringComparison.OrdinalIgnoreCase) ||
                               key.Contains("LiveTv", StringComparison.OrdinalIgnoreCase) ||
                               name.Contains("Guide", StringComparison.OrdinalIgnoreCase) ||
                               name.Contains("Live TV", StringComparison.OrdinalIgnoreCase);
                    });

                if (refreshTask.ValueKind != JsonValueKind.Undefined)
                {
                    _cachedRefreshTaskId = refreshTask.GetProperty("Id").GetString();
                    var taskName = refreshTask.GetProperty("Name").GetString();
                    _logger.LogInformation("Found Jellyfin guide refresh task: '{Name}' (ID: {Id})", taskName, _cachedRefreshTaskId);
                }
                else
                {
                    _logger.LogError("Could not find any guide/Live TV refresh task in Jellyfin. Available tasks: {Tasks}",
                        string.Join(", ", taskNames));
                    return;
                }
            }

            // 2. Trigger the task
            var runUrl = $"{_config.JellyfinUrl.TrimEnd('/')}/ScheduledTasks/Running/{_cachedRefreshTaskId}";
            _logger.LogInformation("Triggering Jellyfin task at {Url}", runUrl);
            var runResponse = await client.PostAsync(runUrl, null, ct);
            
            if (runResponse.IsSuccessStatusCode || runResponse.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                _logger.LogInformation("Jellyfin guide refresh triggered successfully (Status: {Status}).", runResponse.StatusCode);
            }
            else
            {
                var body = await runResponse.Content.ReadAsStringAsync(ct);
                _logger.LogError("Failed to trigger Jellyfin guide refresh. Status: {Status}, Body: {Body}", 
                    runResponse.StatusCode, body);
                // Reset cached ID in case it's stale
                _cachedRefreshTaskId = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing Jellyfin guide");
            // Reset cached ID so we retry discovery next time
            _cachedRefreshTaskId = null;
        }
    }
}
