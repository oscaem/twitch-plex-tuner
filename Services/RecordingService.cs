using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using TwitchPlexTuner.Models;

namespace TwitchPlexTuner.Services;

/// <summary>
/// Background service that monitors live streams and records them using streamlink.
/// Only channels with RecordEnabled=true (from subscriptions.yaml "recording" list) are recorded.
/// Completely decoupled from the streaming/playback pipeline.
/// 
/// Lifecycle:
///   - Runs as a BackgroundService with its own loop
///   - Wakes up when TwitchUpdateService signals new channel data via NotifyChannelsUpdated()
///   - Falls back to a 5-minute timeout if no signal is received (safety net)
///   - Manages streamlink child processes independently
/// </summary>
public class RecordingService : BackgroundService
{
    private readonly TwitchService _twitchService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RecordingService> _logger;
    private readonly string _recordingPath;
    private readonly int _retentionDays;
    
    // Signal from TwitchUpdateService that fresh channel data is available
    private readonly ManualResetEventSlim _updateSignal = new(false);
    
    // Track active recordings to avoid duplicates
    private readonly ConcurrentDictionary<string, RecordingInfo> _activeRecordings = new();
    
    // Ensure only one file is remuxed at a time to keep CPU/IO load calm
    private readonly SemaphoreSlim _remuxSemaphore = new(1, 1);

    public RecordingService(TwitchService twitchService, ILogger<RecordingService> logger, IHttpClientFactory httpClientFactory)
    {
        _twitchService = twitchService;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        
        var envPath = Environment.GetEnvironmentVariable("RECORDING_PATH");
        var defaultContainerPath = "/recordings";

        // Heuristic to detect if a host path (like Synology /volume1) was passed instead of container path
        if (!string.IsNullOrEmpty(envPath) && envPath.StartsWith("/volume", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("⚠️ Potential path mismatch: RECORDING_PATH is set to '{Path}', which looks like a host path. " +
                "Inside the container, you should typically use '{Default}'.", envPath, defaultContainerPath);
        }

        // Use environment variable if provided, otherwise default to /recordings if it exists
        if (!string.IsNullOrEmpty(envPath))
        {
            _recordingPath = envPath;
        }
        else if (Directory.Exists(defaultContainerPath))
        {
            _recordingPath = defaultContainerPath;
            _logger.LogInformation("ℹ️ RECORDING_PATH not set, defaulting to '{Path}'", _recordingPath);
        }
        else
        {
            _recordingPath = string.Empty;
        }
        
        if (!int.TryParse(Environment.GetEnvironmentVariable("RECORDING_RETENTION_DAYS"), out _retentionDays))
        {
            _retentionDays = 30;
        }
    }

    /// <summary>
    /// Called by TwitchUpdateService after each channel data refresh.
    /// Wakes the recording loop to immediately check for new/ended streams.
    /// </summary>
    public void NotifyChannelsUpdated()
    {
        _logger.LogDebug("Recording service received channel update notification");
        _updateSignal.Set();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(_recordingPath))
        {
            _logger.LogWarning("⏹ Recording DISABLED — RECORDING_PATH environment variable is not set. " +
                "Set RECORDING_PATH to a directory path to enable recording.");
            return;
        }

        // Ensure recording directory exists
        try
        {
            Directory.CreateDirectory(_recordingPath);
            _logger.LogInformation("🎬 Recording service STARTED — Output: {Path}, Retention: {Days} days",
                _recordingPath, _retentionDays);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to create recording directory {Path}. Recording disabled.", _recordingPath);
            return;
        }

        var lastCleanup = DateTime.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for signal from TwitchUpdateService, or timeout after 5 minutes (safety net)
                _updateSignal.Wait(TimeSpan.FromMinutes(5), stoppingToken);
                _updateSignal.Reset();

                _logger.LogInformation("🔄 Recording sync triggered — checking live channels...");

                // Run cleanup once per hour
                if (DateTime.UtcNow - lastCleanup > TimeSpan.FromHours(1))
                {
                    CleanupOldRecordings();
                    lastCleanup = DateTime.UtcNow;
                }

                await SyncRecordingsAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Recording service shutting down...");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error in recording service main loop");
                // Don't exit — keep trying
                try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch { break; }
            }
        }

        // Cleanup: stop all recordings on shutdown
        _logger.LogInformation("🛑 Stopping all active recordings ({Count} active)...", _activeRecordings.Count);
        foreach (var kvp in _activeRecordings)
        {
            StopRecording(kvp.Key, "service shutdown");
        }
        _logger.LogInformation("Recording service stopped.");
    }

    private async Task SyncRecordingsAsync(CancellationToken ct)
    {
        var channels = _twitchService.GetChannels();
        var recordableChannels = channels.Where(c => c.IsLive && c.RecordEnabled).ToList();
        var allRecordEnabledChannels = channels.Where(c => c.RecordEnabled).ToList();

        _logger.LogInformation("📊 Channel status: {Total} total, {RecordEnabled} record-enabled, {Live} live & recordable, {ActiveRecordings} currently recording",
            channels.Count,
            allRecordEnabledChannels.Count,
            recordableChannels.Count,
            _activeRecordings.Count);

        if (!allRecordEnabledChannels.Any())
        {
            _logger.LogWarning("⚠️ No channels have recording enabled. Add a 'recording' list to subscriptions.yaml.");
        }

        // 1. Start recordings for newly-live channels
        foreach (var channel in recordableChannels)
        {
            if (_activeRecordings.TryGetValue(channel.Login, out var existing))
            {
                // Check if the recording process is still running
                if (!existing.Process.HasExited)
                {
                    var duration = DateTime.UtcNow - existing.StartedAt;
                    _logger.LogDebug("⏺ [{Login}] Already recording for {Duration:hh\\:mm\\:ss} — {Title}",
                        channel.Login, duration, channel.StreamTitle);
                    continue;
                }

                // Process died — clean up and restart
                var exitCode = existing.Process.ExitCode;
                _logger.LogWarning("⚠️ [{Login}] Recording process died (exit code: {ExitCode}, ran for {Duration:hh\\:mm\\:ss}). Will restart.",
                    channel.Login, exitCode, DateTime.UtcNow - existing.StartedAt);
                _activeRecordings.TryRemove(channel.Login, out _);
            }

            // Start new recording
            StartRecording(channel);
        }

        // 2. Stop recordings for channels that went offline or lost RecordEnabled
        var activeLiveLogins = recordableChannels.Select(c => c.Login).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var login in _activeRecordings.Keys.ToList())
        {
            if (!activeLiveLogins.Contains(login))
            {
                var channel = channels.FirstOrDefault(c => c.Login.Equals(login, StringComparison.OrdinalIgnoreCase));
                var reason = channel == null ? "channel removed from config"
                    : !channel.RecordEnabled ? "recording disabled in config"
                    : !channel.IsLive ? "channel went offline"
                    : "unknown";

                StopRecording(login, reason);
            }
        }

        // 3. Summary
        if (_activeRecordings.Any())
        {
            var summary = _activeRecordings.Select(kvp =>
            {
                var duration = DateTime.UtcNow - kvp.Value.StartedAt;
                var fileSize = GetFileSizeMB(kvp.Value.OutputPath);
                return $"{kvp.Key} ({duration:hh\\:mm\\:ss}, {fileSize:F1}MB)";
            });
            _logger.LogInformation("📹 Active recordings: {Recordings}", string.Join(" | ", summary));
        }

        await Task.CompletedTask;
    }

    private void StartRecording(ChannelInfo channel)
    {
        try
        {
            // Create output directory per channel
            var channelDir = Path.Combine(_recordingPath, SanitizeFilename(channel.DisplayName));
            var seasonDir = Path.Combine(channelDir, "Season Recordings");
            Directory.CreateDirectory(seasonDir);

            // Save profile picture as Jellyfin cover photo (folder.jpg)
            _ = Task.Run(async () =>
            {
                try
                {
                    var coverPath = Path.Combine(channelDir, "folder.jpg");
                    if (!File.Exists(coverPath) && !string.IsNullOrEmpty(channel.ProfileImageUrl))
                    {
                        _logger.LogInformation("📸 [{Login}] Downloading profile picture for cover: {Url}", channel.Login, channel.ProfileImageUrl);
                        using var client = _httpClientFactory.CreateClient();
                        var imageBytes = await client.GetByteArrayAsync(channel.ProfileImageUrl);
                        await File.WriteAllBytesAsync(coverPath, imageBytes);
                        _logger.LogInformation("✅ [{Login}] Saved cover photo to {Path}", channel.Login, coverPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "⚠️ [{Login}] Failed to download profile picture", channel.Login);
                }
            });

            // Generate filename: {Channel} - {Date} - {Title}.ts
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm");
            var title = SanitizeFilename(channel.StreamTitle);
            if (title.Length > 50) title = title[..50];
            
            var filename = $"{channel.DisplayName} - {timestamp} - {title}.ts";
            var outputPath = Path.Combine(seasonDir, filename);

            var quality = Environment.GetEnvironmentVariable("STREAM_QUALITY") ?? "best";
            
            var psi = new ProcessStartInfo
            {
                FileName = "streamlink",
                Arguments = $"--twitch-disable-ads --stream-segment-threads 2 --output \"{outputPath}\" \"twitch.tv/{channel.Login}\" {quality}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _logger.LogInformation("🎬 [{Login}] Starting recording: streamlink {Args}", channel.Login, psi.Arguments);

            var process = new Process { StartInfo = psi };
            
            if (!process.Start())
            {
                _logger.LogError("❌ [{Login}] Failed to start streamlink process", channel.Login);
                return;
            }

            var recordingInfo = new RecordingInfo(process, outputPath, DateTime.UtcNow, channel.StreamTitle);
            _activeRecordings[channel.Login] = recordingInfo;

            _logger.LogInformation("✅ [{Login}] Recording started — PID: {PID}, Output: {Path}, Stream: {Title}",
                channel.Login, process.Id, outputPath, channel.StreamTitle);

            // Log stderr in background for diagnostics
            _ = Task.Run(async () =>
            {
                try
                {
                    using var reader = process.StandardError;
                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        
                        var trimmed = line.Trim();
                        
                        // Promote important messages to Info level
                        if (trimmed.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                            trimmed.Contains("fail", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogWarning("⚠️ [{Login}] streamlink: {Line}", channel.Login, trimmed);
                        }
                        else if (trimmed.Contains("Opening stream") || 
                                 trimmed.Contains("Writing output") ||
                                 trimmed.Contains("Stream ended"))
                        {
                            _logger.LogInformation("📝 [{Login}] streamlink: {Line}", channel.Login, trimmed);
                        }
                        else
                        {
                            _logger.LogDebug("[{Login}] streamlink: {Line}", channel.Login, trimmed);
                        }
                    }
                }
                catch { /* Process ended */ }
                
                // Process exited — log final status
                try
                {
                    var exitCode = process.ExitCode;
                    var fileSize = GetFileSizeMB(outputPath);
                    
                    if (exitCode == 0)
                    {
                        _logger.LogInformation("✅ [{Login}] Recording process exited normally. File: {Path} ({Size:F1}MB)",
                            channel.Login, outputPath, fileSize);
                    }
                    else
                    {
                        _logger.LogWarning("⚠️ [{Login}] Recording process exited with code {ExitCode}. File: {Path} ({Size:F1}MB)",
                            channel.Login, exitCode, outputPath, fileSize);
                    }
                    
                    // Trigger remux asynchronously
                    _ = RemuxToMp4Async(outputPath, channel.Login);
                }
                catch { }
            }, CancellationToken.None);

            // Drain stdout
            _ = Task.Run(async () =>
            {
                try
                {
                    using var reader = process.StandardOutput;
                    while (await reader.ReadLineAsync() != null) { }
                }
                catch { /* Process ended */ }
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ [{Login}] Failed to start recording — is streamlink installed?", channel.Login);
        }
    }

    private void StopRecording(string login, string reason)
    {
        if (_activeRecordings.TryRemove(login, out var info))
        {
            var duration = DateTime.UtcNow - info.StartedAt;
            var fileSize = GetFileSizeMB(info.OutputPath);
            
            try
            {
                if (!info.Process.HasExited)
                {
                    _logger.LogInformation("🛑 [{Login}] Stopping recording — Reason: {Reason}, Duration: {Duration:hh\\:mm\\:ss}, File: {Path} ({Size:F1}MB)",
                        login, reason, duration, info.OutputPath, fileSize);
                    info.Process.Kill(true);
                }
                else
                {
                    _logger.LogInformation("🏁 [{Login}] Recording already ended — Reason: {Reason}, Duration: {Duration:hh\\:mm\\:ss}, Exit code: {ExitCode}, File: {Path} ({Size:F1}MB)",
                        login, reason, duration, info.Process.ExitCode, info.OutputPath, fileSize);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ [{Login}] Error stopping recording process", login);
            }
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
            var totalFreedMB = 0.0;

            _logger.LogInformation("🧹 Running recording cleanup (retention: {Days} days, cutoff: {Cutoff:yyyy-MM-dd HH:mm})",
                _retentionDays, cutoff);

            foreach (var file in files)
            {
                var fi = new FileInfo(file);
                if (fi.CreationTime < cutoff)
                {
                    try
                    {
                        var sizeMB = fi.Length / 1024.0 / 1024.0;
                        fi.Delete();
                        deletedCount++;
                        totalFreedMB += sizeMB;
                        _logger.LogDebug("🗑 Deleted old recording: {Path} ({Size:F1}MB)", file, sizeMB);
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
                _logger.LogInformation("🧹 Cleanup complete: deleted {Count} old recordings, freed {Size:F1}MB",
                    deletedCount, totalFreedMB);
            }
            else
            {
                _logger.LogDebug("Cleanup complete: no old recordings to remove.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error during recording cleanup");
        }
    }

    private static double GetFileSizeMB(string path)
    {
        try
        {
            if (File.Exists(path))
                return new FileInfo(path).Length / 1024.0 / 1024.0;
        }
        catch { }
        return 0;
    }

    private static string SanitizeFilename(string filename)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("", filename.Select(c => invalid.Contains(c) ? '_' : c));
    }

    /// <summary>Check if a channel is currently being recorded.</summary>
    public bool IsRecording(string login) => _activeRecordings.ContainsKey(login);

    /// <summary>Get count of active recordings.</summary>
    public int ActiveRecordingCount => _activeRecordings.Count;

    private async Task RemuxToMp4Async(string tsPath, string login)
    {
        if (string.IsNullOrEmpty(tsPath) || !File.Exists(tsPath)) return;

        var fileSizeMB = GetFileSizeMB(tsPath);
        if (fileSizeMB < 1.0)
        {
            _logger.LogDebug("🗑 [{Login}] File too small to remux ({Size:F1}MB), ignoring: {Path}", login, fileSizeMB, tsPath);
            return;
        }

        var mp4Path = Path.ChangeExtension(tsPath, ".mp4");

        try
        {
            _logger.LogInformation("⏳ [{Login}] Queued for remuxing: {Path}", login, tsPath);
            await _remuxSemaphore.WaitAsync();

            if (!File.Exists(tsPath)) return; // Double check in case it was deleted while waiting

            _logger.LogInformation("🔄 [{Login}] Starting remux to mp4: {Path}", login, mp4Path);

            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-y -i \"{tsPath}\" -c copy \"{mp4Path}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                _logger.LogError("❌ [{Login}] Failed to start ffmpeg process for remuxing.", login);
                return;
            }

            // Lower priority for "calm" CPU usage, if supported by the OS/container
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch { /* Ignore if unauthorized or unsupported */ }

            await process.WaitForExitAsync();

            if (process.ExitCode == 0 && File.Exists(mp4Path) && GetFileSizeMB(mp4Path) > 0)
            {
                _logger.LogInformation("✅ [{Login}] Successfully remuxed to {Path}. Deleting original .ts.", login, mp4Path);
                File.Delete(tsPath);
            }
            else
            {
                var stderr = await process.StandardError.ReadToEndAsync();
                _logger.LogWarning("⚠️ [{Login}] Remux failed with code {Code}. Stderr: {Error}", login, process.ExitCode, stderr);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ [{Login}] Error during remuxing to mp4: {Message}", login, ex.Message);
        }
        finally
        {
            _remuxSemaphore.Release();
        }
    }

    private record RecordingInfo(Process Process, string OutputPath, DateTime StartedAt, string StreamTitle);
}
