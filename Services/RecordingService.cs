using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using TwitchPlexTuner.Models;
using TwitchPlexTuner.Controllers;

namespace TwitchPlexTuner.Services;

/// <summary>
/// Background service that monitors live streams and optionally records them.
/// Recording is triggered when a channel goes live and RECORDING_PATH is set.
/// </summary>
public class RecordingService : BackgroundService
{
    private readonly TwitchService _twitchService;
    private readonly ILogger<RecordingService> _logger;
    private readonly string _recordingPath;
    
    // Track active recordings to avoid duplicates
    private static readonly ConcurrentDictionary<string, Process> _activeRecordings = new();

    public RecordingService(TwitchService twitchService, ILogger<RecordingService> logger)
    {
        _twitchService = twitchService;
        _logger = logger;
        _recordingPath = Environment.GetEnvironmentVariable("RECORDING_PATH") ?? string.Empty;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(_recordingPath))
        {
            _logger.LogInformation("Recording disabled (RECORDING_PATH not set)");
            return;
        }

        _logger.LogInformation("Recording service started. Output: {Path}", _recordingPath);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAndRecordLiveStreamsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in recording service");
            }

            // Check every 2 minutes (separate from guide updates)
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        }

        // Cleanup: stop all recordings on shutdown
        foreach (var kvp in _activeRecordings)
        {
            try
            {
                if (!kvp.Value.HasExited) kvp.Value.Kill(true);
            }
            catch { }
        }
    }

    private async Task CheckAndRecordLiveStreamsAsync(CancellationToken ct)
    {
        var channels = _twitchService.GetChannels();
        
        foreach (var channel in channels.Where(c => c.IsLive))
        {
            // Skip if already recording this channel
            if (_activeRecordings.ContainsKey(channel.Login))
            {
                // Check if the recording process is still running
                if (_activeRecordings.TryGetValue(channel.Login, out var existingProcess))
                {
                    if (!existingProcess.HasExited) continue;
                    _activeRecordings.TryRemove(channel.Login, out _);
                    _logger.LogInformation("[{Login}] Previous recording ended, will restart if still live", channel.Login);
                }
            }

            // Start recording
            _logger.LogInformation("[{Login}] Starting recording for: {Title}", channel.Login, channel.StreamTitle);
            var process = await StartRecordingAsync(channel, ct);
            
            if (process != null)
            {
                _activeRecordings[channel.Login] = process;
            }
        }

        // Clean up recordings for channels that went offline
        var liveLogins = channels.Where(c => c.IsLive).Select(c => c.Login).ToHashSet();
        foreach (var login in _activeRecordings.Keys.ToList())
        {
            if (!liveLogins.Contains(login))
            {
                if (_activeRecordings.TryRemove(login, out var process))
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            _logger.LogInformation("[{Login}] Channel went offline, stopping recording", login);
                            process.Kill(true);
                        }
                    }
                    catch { }
                    
                    // Also invalidate stream cache
                    StreamController.InvalidateCache(login);
                }
            }
        }
    }

    private async Task<Process?> StartRecordingAsync(ChannelInfo channel, CancellationToken ct)
    {
        try
        {
            // Create output directory if needed
            var channelDir = Path.Combine(_recordingPath, SanitizeFilename(channel.DisplayName));
            Directory.CreateDirectory(channelDir);

            // Generate filename with timestamp
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var title = SanitizeFilename(channel.StreamTitle);
            if (title.Length > 50) title = title[..50];
            var filename = $"{timestamp} - {title}.ts";
            var outputPath = Path.Combine(channelDir, filename);

            // Use streamlink for recording (more reliable for long recordings)
            var quality = Environment.GetEnvironmentVariable("STREAM_QUALITY") ?? "best";
            var psi = new ProcessStartInfo
            {
                FileName = "streamlink",
                Arguments = $"twitch.tv/{channel.Login} {quality} -o \"{outputPath}\" --twitch-disable-ads --retry-streams 10 --retry-max 5",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = new Process { StartInfo = psi };
            process.Start();

            _logger.LogInformation("[{Login}] Recording started: {Path}", channel.Login, outputPath);

            // Log errors in background
            _ = Task.Run(async () =>
            {
                var stderr = await process.StandardError.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    _logger.LogWarning("[{Login}] Recording stderr: {Stderr}", channel.Login, stderr.Trim());
                }
            }, CancellationToken.None);

            return process;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Login}] Failed to start recording", channel.Login);
            return null;
        }
    }

    private static string SanitizeFilename(string filename)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("", filename.Select(c => invalid.Contains(c) ? '_' : c));
    }

    // Public method to check recording status
    public static bool IsRecording(string login) => _activeRecordings.ContainsKey(login);
}
