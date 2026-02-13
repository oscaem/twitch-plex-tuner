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
            
            await Response.Body.FlushAsync(HttpContext.RequestAborted);

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
        
        var psi = new ProcessStartInfo
        {
            FileName = "streamlink",
            Arguments = $"--twitch-disable-ads --twitch-low-latency --stdout \"{url}\" {quality}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        
        try
        {
            process.Start();
            
            // Log stderr in background
            _ = Task.Run(async () =>
            {
                var stderr = await process.StandardError.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    // Streamlink is chatty, so maybe only log if it looks like an error or debug enabled
                    _logger.LogDebug("[{Login}] Streamlink stderr: {Stderr}", login, stderr.Trim());
                }
            }, CancellationToken.None);

            // Stream buffer
            var buffer = new byte[65536];
            var stdout = process.StandardOutput.BaseStream;
            
            int bytesRead;
            long totalBytes = 0;
            
            while ((bytesRead = await stdout.ReadAsync(buffer, ct)) > 0)
            {
                await Response.Body.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                totalBytes += bytesRead;
            }

            _logger.LogInformation("[{Login}] Streamlink stream ended normally, sent {TotalMB:F2}MB", login, totalBytes / 1024.0 / 1024.0);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
            catch { /* Process cleanup */ }
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
        // yt-dlp is more stable than streamlink stdout for Plex
        // -q: quiet, --no-warnings: suppress warnings
        // -o -: output to stdout
        var psi = new ProcessStartInfo
        {
            FileName = "yt-dlp",
            Arguments = $"-q --no-warnings \"{streamUrl}\" -o -",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        
        try
        {
            process.Start();
            
            // Log any stderr in background
            _ = Task.Run(async () =>
            {
                var stderr = await process.StandardError.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    _logger.LogWarning("[{Login}] yt-dlp stderr: {Stderr}", login, stderr.Trim());
                }
            }, CancellationToken.None);

            // Stream with larger buffer for DS216+ stability (64KB)
            var buffer = new byte[65536];
            var stdout = process.StandardOutput.BaseStream;
            
            int bytesRead;
            long totalBytes = 0;
            
            while ((bytesRead = await stdout.ReadAsync(buffer, ct)) > 0)
            {
                await Response.Body.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
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
                if (!process.HasExited)
                {
                    process.Kill(true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
            catch { /* Process cleanup */ }
        }
    }

    // Clear cache for a specific login (called when we know stream state changed)
    public static void InvalidateCache(string login)
    {
        _streamUrlCache.TryRemove(login, out _);
    }
    
    private record CachedStreamUrl(string Url, DateTime CachedAt);
}
