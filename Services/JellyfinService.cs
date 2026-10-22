using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net;
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
            _logger.LogDebug("Jellyfin guide refresh enabled for {Url}", _config.JellyfinUrl);
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
            client.DefaultRequestHeaders.Authorization = BuildMediaBrowserAuthorization(_config.JellyfinApiKey);

            // 1. Find the task ID if not cached
            if (string.IsNullOrEmpty(_cachedRefreshTaskId))
            {
                var tasksUrl = $"{_config.JellyfinUrl.TrimEnd('/')}/ScheduledTasks";
                using var response = await SendWithRetryAsync(() => client.GetAsync(tasksUrl, ct), ct);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to fetch Jellyfin guide task. Status: {Status}", response.StatusCode);
                    return;
                }

                var content = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(content);

                var refreshTask = doc.RootElement.EnumerateArray()
                    .FirstOrDefault(t => GetStringProperty(t, "Key")
                        .Equals("RefreshGuide", StringComparison.OrdinalIgnoreCase));

                if (refreshTask.ValueKind == JsonValueKind.Undefined)
                    refreshTask = doc.RootElement.EnumerateArray()
                        .FirstOrDefault(t => GetStringProperty(t, "Name")
                            .Equals("Refresh Guide", StringComparison.OrdinalIgnoreCase));

                if (refreshTask.ValueKind != JsonValueKind.Undefined)
                {
                    _cachedRefreshTaskId = GetStringProperty(refreshTask, "Id");
                    if (string.IsNullOrWhiteSpace(_cachedRefreshTaskId))
                    {
                        _logger.LogError("Jellyfin Refresh Guide task did not include an ID.");
                        return;
                    }
                }
                else
                {
                    _logger.LogError("Could not find the Jellyfin Refresh Guide task.");
                    return;
                }
            }

            var runUrl = $"{_config.JellyfinUrl.TrimEnd('/')}/ScheduledTasks/Running/{_cachedRefreshTaskId}";
            using var runResponse = await SendWithRetryAsync(() => client.PostAsync(runUrl, null, ct), ct);
            
            if (runResponse.IsSuccessStatusCode || runResponse.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                _logger.LogDebug("Jellyfin guide refresh triggered successfully (Status: {Status}).", runResponse.StatusCode);
            }
            else
            {
                var body = await runResponse.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Failed to trigger Jellyfin guide refresh. Status: {Status}, Body: {Body}",
                    runResponse.StatusCode, body);
                _cachedRefreshTaskId = null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing Jellyfin guide");
            _cachedRefreshTaskId = null;
        }
    }

    private static string GetStringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) ? property.GetString() ?? string.Empty : string.Empty;

    private static async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<Task<HttpResponseMessage>> send,
        CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await send();
            }
            catch (Exception ex) when (attempt < 2 && (ex is HttpRequestException || ex is IOException))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), ct);
                continue;
            }

            var shouldRetry = attempt < 2 &&
                (response.StatusCode == HttpStatusCode.TooManyRequests ||
                 (int)response.StatusCode >= 500);
            if (!shouldRetry)
                return response;

            response.Dispose();
            await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), ct);
        }
    }

    private static AuthenticationHeaderValue BuildMediaBrowserAuthorization(string apiKey)
    {
        static string Quote(string value) =>
            $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

        var parameters = string.Join(", ",
            $"Client={Quote("TwitchPlexTuner")}",
            $"Device={Quote("Server")}",
            $"DeviceId={Quote("twitch-plex-tuner")}",
            $"Version={Quote("1.0")}",
            $"Token={Quote(apiKey)}");

        return new AuthenticationHeaderValue("MediaBrowser", parameters);
    }
}
