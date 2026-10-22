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
    private const int DefaultPassiveDownloadSegmentThreads = 8;
    private const int MaxPassiveDownloadSegmentThreads = 32;

    private readonly TwitchService _twitchService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RecordingService> _logger;
    private readonly string _recordingPath;
    private readonly int _retentionDays;
    private readonly int _passiveDownloadSegmentThreads;
    private readonly string _twitchOAuthToken;
    
    // Signal from TwitchUpdateService that fresh channel data is available
    private readonly ManualResetEventSlim _updateSignal = new(false);
    
    // Track active recordings to avoid duplicates
    private readonly ConcurrentDictionary<string, RecordingInfo> _activeRecordings = new();
    private readonly Dictionary<string, DateTime> _passiveRetryAfter = new(StringComparer.Ordinal);
    private PassiveRecordingInfo? _activePassiveRecording;
    private int _nextPassiveEntryIndex;
    
    public RecordingService(TwitchService twitchService, ILogger<RecordingService> logger, IHttpClientFactory httpClientFactory, IOptions<TwitchConfig> config)
    {
        _twitchService = twitchService;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _twitchOAuthToken = config.Value.TwitchOAuthToken;
        _passiveDownloadSegmentThreads = GetPassiveDownloadSegmentThreads();
        
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
        
        var retentionDaysSetting = Environment.GetEnvironmentVariable("RECORDING_RETENTION_DAYS");
        if (string.IsNullOrWhiteSpace(retentionDaysSetting))
        {
            _retentionDays = 0;
        }
        else if (!int.TryParse(retentionDaysSetting, out _retentionDays) || _retentionDays < 0)
        {
            _logger.LogWarning("Invalid RECORDING_RETENTION_DAYS value '{Value}'. Retention cleanup is disabled.", retentionDaysSetting);
            _retentionDays = 0;
        }
    }

    /// <summary>
    /// Called by TwitchUpdateService after each channel data refresh.
    /// Wakes the recording loop to immediately check for new/ended streams.
    /// </summary>
    public void NotifyChannelsUpdated()
    {
        _logger.LogDebug("Recording service received channel update notification");
        if (_activePassiveRecording is { } active && !active.Process.HasExited)
        {
            _logger.LogInformation("Passive VOD download is still in progress for {Name}/{VideoId}.",
                active.Name, active.VideoId);
        }
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

                _logger.LogDebug("Recording sync triggered — checking live channels.");

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
        StopPassiveRecording("service shutdown");
        _logger.LogInformation("Recording service stopped.");
    }

    private async Task SyncRecordingsAsync(CancellationToken ct)
    {
        var channels = _twitchService.GetChannels();
        var recordableChannels = channels.Where(c => c.IsLive && c.RecordEnabled).ToList();
        var allRecordEnabledChannels = channels.Where(c => c.RecordEnabled).ToList();

        _logger.LogDebug("Channel status: {Total} total, {RecordEnabled} record-enabled, {Live} live & recordable, {ActiveRecordings} currently recording",
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

        await SyncPassiveRecordingsAsync(ct);
    }

    private async Task SyncPassiveRecordingsAsync(CancellationToken ct)
    {
        CompletePassiveRecordingIfExited();
        if (_activePassiveRecording != null)
        {
            if (HasRunningMainRecording())
                StopPassiveRecording("main-channel recording takes priority");
            return;
        }

        if (HasRunningMainRecording())
        {
            _logger.LogDebug("Passive VOD downloads are paused while a main-channel recording is active.");
            return;
        }

        var entries = _twitchService.GetPassiveRecordEntries();
        if (entries.Count == 0)
            return;

        var startIndex = _nextPassiveEntryIndex % entries.Count;
        _nextPassiveEntryIndex = (startIndex + 1) % entries.Count;
        for (var offset = 0; offset < entries.Count; offset++)
        {
            var entry = entries[(startIndex + offset) % entries.Count];
            ct.ThrowIfCancellationRequested();
            List<TwitchVideo> videos;
            try
            {
                videos = await _twitchService.GetAvailableVideosAsync(entry.Login, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Unable to check recent Twitch VODs for passive recording entry {Name}", entry.Name);
                continue;
            }

            foreach (var video in videos.OrderByDescending(video => video.CreatedAt))
            {
                var retryKey = $"{entry.Login}/{video.Id}";
                if (_passiveRetryAfter.TryGetValue(retryKey, out var retryAt))
                {
                    if (retryAt > DateTime.UtcNow)
                        continue;
                    _passiveRetryAfter.Remove(retryKey);
                }

                var outputPath = GetPassiveRecordingPath(entry, video);
                var archivePath = PassiveVodArchive.GetPath(Path.GetDirectoryName(outputPath)!);
                HashSet<string> archivedIds;
                try
                {
                    archivedIds = PassiveVodArchive.ReadIds(archivePath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unable to read passive VOD archive for {Name}; skipping this streamer to avoid duplicate downloads.",
                        entry.Name);
                    break;
                }

                if (archivedIds.Contains(video.Id))
                    continue;

                if (File.Exists(outputPath))
                {
                    try
                    {
                        PassiveVodArchive.AppendId(archivePath, video.Id);
                        _logger.LogInformation("Added existing VOD {VideoId} to passive archive for {Name}", video.Id, entry.Name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Unable to persist VOD {VideoId} in passive archive for {Name}", video.Id, entry.Name);
                        break;
                    }
                    continue;
                }
                if (File.Exists(outputPath + ".partial"))
                {
                    _logger.LogDebug("Removing incomplete passive VOD download before retry: {Path}", outputPath + ".partial");
                    File.Delete(outputPath + ".partial");
                }

                if (StartPassiveRecording(entry, video, outputPath, archivePath))
                    return;
                _passiveRetryAfter[retryKey] = DateTime.UtcNow.AddMinutes(5);
            }
        }
    }

    private string GetPassiveRecordingPath(PassiveRecordEntry entry, TwitchVideo video)
    {
        var streamerDirectory = string.IsNullOrWhiteSpace(entry.Group)
            ? Path.Combine(_recordingPath, SanitizeFilename(entry.Name))
            : Path.Combine(_recordingPath, SanitizeFilename(entry.Group), SanitizeFilename(entry.Name));
        Directory.CreateDirectory(streamerDirectory);

        var title = SanitizeFilename(video.Title);
        if (title.Length > 80) title = title[..80];
        var fileName = $"{entry.Name} - {video.CreatedAt.ToLocalTime():yyyy-MM-dd_HH-mm} - {title} [{video.Id}].ts";
        return Path.Combine(streamerDirectory, SanitizeFilename(fileName));
    }

    private bool StartPassiveRecording(PassiveRecordEntry entry, TwitchVideo video, string outputPath, string archivePath)
    {
        try
        {
            var partialPath = outputPath + ".partial";
            var quality = Environment.GetEnvironmentVariable("STREAM_QUALITY") ?? "best";
            var psi = new ProcessStartInfo
            {
                FileName = "streamlink",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            StreamlinkArguments.AddTwitchOAuthHeader(psi, _twitchOAuthToken);
            psi.ArgumentList.Add("--stream-segment-threads");
            psi.ArgumentList.Add(_passiveDownloadSegmentThreads.ToString());
            psi.ArgumentList.Add("--output");
            psi.ArgumentList.Add(partialPath);
            psi.ArgumentList.Add(video.Url);
            psi.ArgumentList.Add(quality);

            var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                _logger.LogError("Failed to start passive recording for {Name} VOD {VideoId}", entry.Name, video.Id);
                return false;
            }

            _activePassiveRecording = new PassiveRecordingInfo(process, partialPath, outputPath, archivePath, entry.Name, entry.Login, video.Id);
            _logger.LogDebug("Started passive VOD download for {Name}: {Title} ({VideoId}); output: {Path}; quality: {Quality}; segment threads: {Threads}",
                entry.Name, video.Title, video.Id, outputPath, quality, _passiveDownloadSegmentThreads);

            _ = DrainProcessOutputAsync(process, entry.Name, video.Id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start passive recording for {Name} VOD {VideoId}", entry.Name, video.Id);
            return false;
        }
    }

    private async Task DrainProcessOutputAsync(Process process, string name, string videoId)
    {
        try
        {
            var stderrTask = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync()) != null)
                    if (!string.IsNullOrWhiteSpace(line))
                        _logger.LogDebug("[Passive {Name}/{VideoId}] {Line}", name, videoId, line.Trim());
            });
            var stdoutTask = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync() != null) { }
            });
            await Task.WhenAll(stderrTask, stdoutTask);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error reading streamlink output for passive VOD {VideoId}", videoId);
        }
    }

    private void CompletePassiveRecordingIfExited()
    {
        var active = _activePassiveRecording;
        if (active == null || !active.Process.HasExited)
            return;

        _activePassiveRecording = null;
        try
        {
            if (active.Process.ExitCode == 0 && File.Exists(active.PartialPath))
            {
                File.Move(active.PartialPath, active.OutputPath, overwrite: true);
                PassiveVodArchive.AppendId(active.ArchivePath, active.VideoId);
                _logger.LogInformation("Completed and archived Twitch VOD ID {VideoId} for {Name}: {Path}",
                    active.VideoId, active.Name, active.OutputPath);
            }
            else
            {
                _logger.LogWarning("Passive VOD backfill for {Name}/{VideoId} failed with exit code {ExitCode}",
                    active.Name, active.VideoId, active.Process.ExitCode);
                _passiveRetryAfter[$"{active.Login}/{active.VideoId}"] = DateTime.UtcNow.AddMinutes(5);
                if (File.Exists(active.PartialPath))
                    File.Delete(active.PartialPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to finalize passive VOD backfill for {Name}/{VideoId}", active.Name, active.VideoId);
        }
        finally
        {
            active.Process.Dispose();
        }
    }

    private void StopPassiveRecording(string reason)
    {
        var active = _activePassiveRecording;
        _activePassiveRecording = null;
        if (active == null)
            return;

        try
        {
            if (!active.Process.HasExited)
            {
                _logger.LogInformation("Stopping passive VOD backfill {VideoId}: {Reason}", active.VideoId, reason);
                active.Process.Kill(true);
                active.Process.WaitForExit();
            }
            active.Process.Dispose();
            if (File.Exists(active.PartialPath))
                File.Delete(active.PartialPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping passive VOD backfill {VideoId}", active.VideoId);
        }
    }

    private bool HasRunningMainRecording() =>
        _activeRecordings.Values.Any(recording => !recording.Process.HasExited);

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
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            StreamlinkArguments.AddTwitchAdFiltering(psi);
            StreamlinkArguments.AddTwitchOAuthHeader(psi, _twitchOAuthToken);
            psi.ArgumentList.Add("--stream-segment-threads");
            psi.ArgumentList.Add("2");
            psi.ArgumentList.Add("--output");
            psi.ArgumentList.Add(outputPath);
            psi.ArgumentList.Add($"twitch.tv/{channel.Login}");
            psi.ArgumentList.Add(quality);

            _logger.LogInformation("🎬 [{Login}] Starting recording: streamlink {Args}", channel.Login, StreamlinkArguments.ToSafeLogString(psi));

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

            var channels = _twitchService.GetChannels();
            var passiveEntries = _twitchService.GetPassiveRecordEntries();
            var files = Directory.GetFiles(_recordingPath, "*.*", SearchOption.AllDirectories)
                .Where(file => !Path.GetFileName(file).Equals(PassiveVodArchive.FileName, StringComparison.Ordinal));
            var deletedCount = 0;
            var totalFreedMB = 0.0;

            if (_retentionDays == 0 &&
                !channels.Any(channel => channel.RetentionDays > 0) &&
                !passiveEntries.Any(entry => entry.RetentionDays > 0))
            {
                _logger.LogDebug("Recording cleanup disabled: global and per-streamer retention are all zero or unset.");
                return;
            }

            _logger.LogDebug("Running recording cleanup (global retention: {Days} days)", _retentionDays);

            foreach (var file in files)
            {
                var fi = new FileInfo(file);
                var retentionDays = GetRetentionDaysForFile(file, channels, passiveEntries);
                if (retentionDays > 0 && fi.CreationTime < DateTime.Now.AddDays(-retentionDays))
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

    private int GetRetentionDaysForFile(
        string filePath,
        IReadOnlyCollection<ChannelInfo> channels,
        IReadOnlyCollection<PassiveRecordEntry> passiveEntries)
    {
        var relativePath = Path.GetRelativePath(_recordingPath, filePath);
        var pathParts = relativePath.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        if (pathParts.Length >= 3 &&
            pathParts[1].Equals("Season Recordings", StringComparison.OrdinalIgnoreCase))
        {
            var channel = channels.FirstOrDefault(item =>
                SanitizeFilename(item.DisplayName).Equals(pathParts[0], StringComparison.OrdinalIgnoreCase));
            if (channel != null)
                return channel.RetentionDays ?? _retentionDays;
        }

        foreach (var entry in passiveEntries)
        {
            var isGroupedPath = !string.IsNullOrWhiteSpace(entry.Group) &&
                pathParts.Length >= 3 &&
                SanitizeFilename(entry.Group).Equals(pathParts[0], StringComparison.OrdinalIgnoreCase) &&
                SanitizeFilename(entry.Name).Equals(pathParts[1], StringComparison.OrdinalIgnoreCase);
            var isUngroupedPath = string.IsNullOrWhiteSpace(entry.Group) &&
                pathParts.Length >= 2 &&
                SanitizeFilename(entry.Name).Equals(pathParts[0], StringComparison.OrdinalIgnoreCase);
            if (isGroupedPath || isUngroupedPath)
                return entry.RetentionDays ?? _retentionDays;
        }

        return _retentionDays;
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

    private int GetPassiveDownloadSegmentThreads()
    {
        var setting = Environment.GetEnvironmentVariable("PASSIVE_DOWNLOAD_SEGMENT_THREADS");
        if (string.IsNullOrWhiteSpace(setting))
            return DefaultPassiveDownloadSegmentThreads;

        if (int.TryParse(setting, out var threads) && threads is >= 1 and <= MaxPassiveDownloadSegmentThreads)
            return threads;

        _logger.LogWarning(
            "Invalid PASSIVE_DOWNLOAD_SEGMENT_THREADS value '{Value}'. Using default of {Default} (allowed range: 1-{Maximum}).",
            setting, DefaultPassiveDownloadSegmentThreads, MaxPassiveDownloadSegmentThreads);
        return DefaultPassiveDownloadSegmentThreads;
    }

    /// <summary>Check if a channel is currently being recorded.</summary>
    public bool IsRecording(string login) => _activeRecordings.ContainsKey(login);

    /// <summary>Get count of active recordings.</summary>
    public int ActiveRecordingCount => _activeRecordings.Count;

    private record RecordingInfo(Process Process, string OutputPath, DateTime StartedAt, string StreamTitle);
    private record PassiveRecordingInfo(
        Process Process,
        string PartialPath,
        string OutputPath,
        string ArchivePath,
        string Name,
        string Login,
        string VideoId);
}
