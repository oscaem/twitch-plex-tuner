using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using TwitchPlexTuner.Models;
using TwitchPlexTuner.Controllers;

namespace TwitchPlexTuner.Services;

/// <summary>
/// Background service that monitors live streams and records them using streamlink.
/// Only channels with RecordEnabled=true (from subscriptions.yaml "recording" list) are recorded.
/// Completely decoupled from the streaming/playback pipeline.
/// </summary>
public class RecordingService : BackgroundService
{
    private readonly TwitchService _twitchService;
    private readonly ILogger<RecordingService> _logger;
    private readonly string _recordingPath;
    private readonly int _retentionDays;
    
    // Track active recordings to avoid duplicates
    private static readonly ConcurrentDictionary<string, Process> _activeRecordings = new();

    public RecordingService(TwitchService twitchService, ILogger<RecordingService> logger)
    {
        _twitchService = twitchService;
        _logger = logger;
        _recordingPath = Environment.GetEnvironmentVariable("RECORDING_PATH") ?? string.Empty;
        
        if (!int.TryParse(Environment.GetEnvironmentVariable("RECORDING_RETENTION_DAYS"), out _retentionDays))
        {
            _retentionDays = 30; // Default to 30 days
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(_recordingPath))
        {
            _logger.LogInformation("Recording disabled (RECORDING_PATH not set)");
            return;
        }

        // Ensure recording directory exists
        Directory.CreateDirectory(_recordingPath);

        _logger.LogInformation("Recording service started. Output: {Path}, Retention: {Days} days", _recordingPath, _retentionDays);

        var lastCleanup = DateTime.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Run cleanup once per hour
                if (DateTime.UtcNow - lastCleanup > TimeSpan.FromHours(1))
                {
                    CleanupOldRecordings();
                    lastCleanup = DateTime.UtcNow;
                }

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
        var recordableChannels = channels.Where(c => c.IsLive && c.RecordEnabled).ToList();
        
        if (recordableChannels.Any())
        {
            _logger.LogDebug("Live channels eligible for recording: {Channels}", 
                string.Join(", ", recordableChannels.Select(c => c.Login)));
        }

        foreach (var channel in recordableChannels)
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
            var process = StartRecording(channel);
            
            if (process != null)
            {
                _activeRecordings[channel.Login] = process;
            }
        }

        // Clean up recordings for channels that went offline or lost RecordEnabled
        var activeLiveLogins = recordableChannels.Select(c => c.Login).ToHashSet();
        foreach (var login in _activeRecordings.Keys.ToList())
        {
            if (!activeLiveLogins.Contains(login))
            {
                if (_activeRecordings.TryRemove(login, out var process))
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            _logger.LogInformation("[{Login}] Channel went offline or recording disabled, stopping recording", login);
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

    private Process? StartRecording(ChannelInfo channel)
    {
        try
        {
            // Create output directory per channel
            var channelDir = Path.Combine(_recordingPath, SanitizeFilename(channel.DisplayName));
            Directory.CreateDirectory(channelDir);

            // Generate filename: {Channel} - {Date} - {Title}.ts
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm");
            var title = SanitizeFilename(channel.StreamTitle);
            if (title.Length > 50) title = title[..50];
            
            var filename = $"{channel.DisplayName} - {timestamp} - {title}.ts";
            var outputPath = Path.Combine(channelDir, filename);

            // Use streamlink for recording — it handles live HLS natively and writes directly to file.
            // This is completely separate from the streaming pipeline (StreamController uses its own processes).
            var quality = Environment.GetEnvironmentVariable("STREAM_QUALITY") ?? "best";
            
            var psi = new ProcessStartInfo
            {
                FileName = "streamlink",
                Arguments = $"--twitch-disable-ads --hls-live-edge 3 --hls-segment-threads 2 --output \"{outputPath}\" \"twitch.tv/{channel.Login}\" {quality}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = new Process { StartInfo = psi };
            process.Start();

            _logger.LogInformation("[{Login}] Recording started → {Path} (PID: {PID})", channel.Login, outputPath, process.Id);

            // Log stderr in background for diagnostics
            _ = Task.Run(async () =>
            {
                try
                {
                    using var reader = process.StandardError;
                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            _logger.LogDebug("[{Login}] Recording: {Line}", channel.Login, line.Trim());
                        }
                    }
                }
                catch { /* Process ended */ }
            }, CancellationToken.None);

            // Drain stdout (streamlink may write progress info)
            _ = Task.Run(async () =>
            {
                try
                {
                    using var reader = process.StandardOutput;
                    while (await reader.ReadLineAsync() != null) { }
                }
                catch { /* Process ended */ }
            }, CancellationToken.None);

            return process;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Login}] Failed to start recording", channel.Login);
            return null;
        }
    }

    private void CleanupOldRecordings()
    {
        try
        {
            if (!Directory.Exists(_recordingPath)) return;

            var cutoff = DateTime.Now.AddDays(-_retentionDays);
            var files = Directory.GetFiles(_recordingPath, "*.*", SearchOption.AllDirectories);
            var deletedCount = 0;

            foreach (var file in files)
            {
                var fi = new FileInfo(file);
                if (fi.CreationTime < cutoff)
                {
                    try
                    {
                        fi.Delete();
                        deletedCount++;
                        _logger.LogDebug("Deleted old recording: {Path}", file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete old recording: {Path}", file);
                    }
                }
            }

            // Clean up empty directories
            foreach (var dir in Directory.GetDirectories(_recordingPath))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir);
                        _logger.LogDebug("Removed empty recording directory: {Dir}", dir);
                    }
                }
                catch { }
            }

            if (deletedCount > 0)
            {
                _logger.LogInformation("Cleanup completed. Deleted {Count} old recordings.", deletedCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during recording cleanup");
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
