using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using TwitchPlexTuner.Models;

namespace TwitchPlexTuner.Controllers;

[ApiController]
public class StreamController : ControllerBase
{
    private readonly ILogger<StreamController> _logger;
    private readonly TwitchConfig _config;
    
    // Cache stream URLs by login to avoid re-discovery for same stream session
    private static readonly ConcurrentDictionary<string, CachedStreamUrl> _streamUrlCache = new();
    
    public StreamController(ILogger<StreamController> logger, IOptions<TwitchConfig> config)
    {
        _logger = logger;
        _config = config.Value;
    }

    [HttpGet("stream/{login}")]
    public async Task GetStream(string login)
    {
        _logger.LogInformation("[{Login}] Stream request received. Engine: {Engine}", login, _config.StreamEngine);
        var sw = Stopwatch.StartNew();

        try
        {
            // 1. Send headers IMMEDIATELY to satisfy Plex's connection timer
            Response.ContentType = "video/mp2t";
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["Pragma"] = "no-cache";
            Response.Headers["Expires"] = "0";
            
            var responseBodyFeature = HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
            responseBodyFeature?.DisableBuffering();
            
            await Task.CompletedTask; // Headers are already set

            if (_config.StreamEngine.Equals("yt-dlp", StringComparison.OrdinalIgnoreCase))
            {
                 // 2a. Legacy yt-dlp path
                string streamUrl = await GetStreamUrlAsync(login, HttpContext.RequestAborted);
                
                if (string.IsNullOrEmpty(streamUrl))
                {
                    _logger.LogWarning("[{Login}] No stream URL found - channel may be offline", login);
                    return;
                }

                _logger.LogInformation("[{Login}] URL obtained in {Elapsed}ms, starting yt-dlp stream", login, sw.ElapsedMilliseconds);
                await StreamWithYtDlpAsync(login, streamUrl, HttpContext.RequestAborted);
            }
            else
            {
                // 2b. Default Streamlink path (better ad handling)
                await StreamWithStreamlinkAsync(login, HttpContext.RequestAborted);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[{Login}] Stream cancelled by client after {Elapsed}ms", login, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Login}] Stream error after {Elapsed}ms", login, sw.ElapsedMilliseconds);
            if (!Response.HasStarted)
            {
                Response.StatusCode = 500;
            }
        }
    }

    private async Task StreamWithStreamlinkAsync(string login, CancellationToken ct)
    {
        var url = $"twitch.tv/{login}";
        var quality = Environment.GetEnvironmentVariable("STREAM_QUALITY") ?? "1080p60,1080p,720p60,720p,best";
        var slPsi = new ProcessStartInfo
        {
            FileName = "streamlink",
            // Start with only 1 segment (~2s buffer) for fastest startup to satisfy probers.
            Arguments = $"--twitch-disable-ads --hls-live-edge 1 --hls-segment-threads 4 --hls-segment-attempts 5 --ringbuffer-size 32M --stdout \"{url}\" {quality}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var slProcess = new Process { StartInfo = slPsi };
        Process? ffmpegProcess = null;

        try
        {
            slProcess.Start();

            // Background logging for streamlink stderr
            _ = Task.Run(async () =>
            {
                var stderr = await slProcess.StandardError.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    _logger.LogDebug("[{Login}] Streamlink stderr: {Stderr}", login, stderr.Trim());
                }
            }, CancellationToken.None);

            Stream inputStream;

            if (!string.IsNullOrEmpty(_config.FFmpegArgs))
            {
                // Chain: Streamlink -> FFmpeg -> Response
                _logger.LogInformation("[{Login}] Starting FFmpeg pipeline with args: {Args}", login, _config.FFmpegArgs);

                var ffmpegPsi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = _config.FFmpegArgs,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                ffmpegProcess = new Process { StartInfo = ffmpegPsi };
                ffmpegProcess.Start();

                // Log ffmpeg stderr
                _ = Task.Run(async () =>
                {
                    var stderr = await ffmpegProcess.StandardError.ReadToEndAsync();
                    if (!string.IsNullOrWhiteSpace(stderr) && !stderr.Contains("frame="))
                    {
                        _logger.LogWarning("[{Login}] FFmpeg stderr: {Stderr}", login, stderr.Trim());
                    }
                }, CancellationToken.None);

                // Pipe Streamlink -> FFmpeg
                var slStdout = slProcess.StandardOutput.BaseStream;
                var ffmpegStdin = ffmpegProcess.StandardInput.BaseStream;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await slStdout.CopyToAsync(ffmpegStdin, ct);
                        ffmpegStdin.Close();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[{Login}] Error piping Streamlink -> FFmpeg", login);
                    }
                }, CancellationToken.None);

                inputStream = ffmpegProcess.StandardOutput.BaseStream;
            }
            else
            {
                // Direct: Streamlink -> Response
                inputStream = slProcess.StandardOutput.BaseStream;
            }

            // Pipe Input (FFmpeg or Streamlink) -> Response
            var buffer = new byte[128 * 1024]; // 128KB: Steady delivery for DS216+
            int bytesRead;
            long totalBytes = 0;

            while ((bytesRead = await inputStream.ReadAsync(buffer, ct)) > 0)
            {
                await Response.Body.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                await Response.Body.FlushAsync(ct); 
                totalBytes += bytesRead;
            }

            _logger.LogInformation("[{Login}] Stream ended normally, sent {TotalMB:F2}MB", login, totalBytes / 1024.0 / 1024.0);
        }
        finally
        {
            try
            {
                if (!slProcess.HasExited) { slProcess.Kill(true); await slProcess.WaitForExitAsync(CancellationToken.None); }
                if (ffmpegProcess != null && !ffmpegProcess.HasExited) { ffmpegProcess.Kill(true); await ffmpegProcess.WaitForExitAsync(CancellationToken.None); }
            }
            catch { /* Cleanup */ }
        }
    }

    private async Task<string> GetStreamUrlAsync(string login, CancellationToken ct)
    {
        var url = $"twitch.tv/{login}";
        
        // Check cache first
        if (_streamUrlCache.TryGetValue(login, out var cached))
        {
            // Cache valid for 5 minutes (stream URLs don't change mid-stream)
            if (DateTime.UtcNow - cached.CachedAt < TimeSpan.FromMinutes(5))
            {
                _logger.LogDebug("[{Login}] Using cached stream URL", login);
                return cached.Url;
            }
            _streamUrlCache.TryRemove(login, out _);
        }

        // Use streamlink for URL discovery (fast, reliable)
        // Quality: prefer 1080p but fall back gracefully
        var quality = Environment.GetEnvironmentVariable("STREAM_QUALITY") ?? "1080p60,1080p,720p60,720p,best";
        
        var psi = new ProcessStartInfo
        {
            FileName = "streamlink",
            Arguments = $"{url} {quality} --stream-url --twitch-disable-ads",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var streamUrl = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        streamUrl = streamUrl.Trim();
        
        if (!string.IsNullOrEmpty(streamUrl) && streamUrl.StartsWith("http"))
        {
            // Cache the URL
            _streamUrlCache[login] = new CachedStreamUrl(streamUrl, DateTime.UtcNow);
            _logger.LogDebug("[{Login}] Cached new stream URL", login);
        }

        return streamUrl;
    }

    private async Task StreamWithYtDlpAsync(string login, string streamUrl, CancellationToken ct)
    {
        // yt-dlp is more stable than streamlink stdout for Plex/Jellyfin
        // -q: quiet, --no-warnings: suppress warnings
        // -o -: output to stdout
        var slPsi = new ProcessStartInfo
        {
            FileName = "yt-dlp",
            Arguments = $"-q --no-warnings \"{streamUrl}\" -o -",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var slProcess = new Process { StartInfo = slPsi };
        Process? ffmpegProcess = null;

        try
        {
            slProcess.Start();

            // Background logging for yt-dlp stderr
            _ = Task.Run(async () =>
            {
                var stderr = await slProcess.StandardError.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    _logger.LogWarning("[{Login}] yt-dlp stderr: {Stderr}", login, stderr.Trim());
                }
            }, CancellationToken.None);

            Stream inputStream;

            if (!string.IsNullOrEmpty(_config.FFmpegArgs))
            {
                // Chain: yt-dlp -> FFmpeg -> Response
                _logger.LogInformation("[{Login}] Starting FFmpeg pipeline with args: {Args}", login, _config.FFmpegArgs);

                var ffmpegPsi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = _config.FFmpegArgs,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                ffmpegProcess = new Process { StartInfo = ffmpegPsi };
                ffmpegProcess.Start();

                // Log ffmpeg stderr
                _ = Task.Run(async () =>
                {
                    var stderr = await ffmpegProcess.StandardError.ReadToEndAsync();
                    if (!string.IsNullOrWhiteSpace(stderr) && !stderr.Contains("frame="))
                    {
                        _logger.LogWarning("[{Login}] FFmpeg stderr: {Stderr}", login, stderr.Trim());
                    }
                }, CancellationToken.None);

                // Pipe yt-dlp -> FFmpeg
                var slStdout = slProcess.StandardOutput.BaseStream;
                var ffmpegStdin = ffmpegProcess.StandardInput.BaseStream;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await slStdout.CopyToAsync(ffmpegStdin, ct);
                        ffmpegStdin.Close();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[{Login}] Error piping yt-dlp -> FFmpeg", login);
                    }
                }, CancellationToken.None);

                inputStream = ffmpegProcess.StandardOutput.BaseStream;
            }
            else
            {
                // Direct: yt-dlp -> Response
                inputStream = slProcess.StandardOutput.BaseStream;
            }

            // Pipe Input (FFmpeg or yt-dlp) -> Response
            var buffer = new byte[128 * 1024]; // 128KB: Steady delivery for DS216+
            int bytesRead;
            long totalBytes = 0;
            bool firstByteSent = false;

            while ((bytesRead = await inputStream.ReadAsync(buffer, ct)) > 0)
            {
                if (!firstByteSent)
                {
                    _logger.LogInformation("[{Login}] First {Bytes} bytes received from input stream. Delivering to client...", login, bytesRead);
                    firstByteSent = true;
                }

                await Response.Body.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                await Response.Body.FlushAsync(ct); 
                totalBytes += bytesRead;
            }

            _logger.LogInformation("[{Login}] Stream ended normally, sent {TotalMB:F2}MB", login, totalBytes / 1024.0 / 1024.0);
            
            // Stream ended - invalidate cache
            _streamUrlCache.TryRemove(login, out _);
        }
        finally
        {
            try
            {
                if (!slProcess.HasExited) { slProcess.Kill(true); await slProcess.WaitForExitAsync(CancellationToken.None); }
                if (ffmpegProcess != null && !ffmpegProcess.HasExited) { ffmpegProcess.Kill(true); await ffmpegProcess.WaitForExitAsync(CancellationToken.None); }
            }
            catch { /* Cleanup */ }
        }
    }

    // Clear cache for a specific login (called when we know stream state changed)
    public static void InvalidateCache(string login)
    {
        _streamUrlCache.TryRemove(login, out _);
    }
    
    private record CachedStreamUrl(string Url, DateTime CachedAt);
}
