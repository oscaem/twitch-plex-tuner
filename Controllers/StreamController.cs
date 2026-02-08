using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace TwitchPlexTuner.Controllers;

[ApiController]
public class StreamController : ControllerBase
{
    private readonly ILogger<StreamController> _logger;

    public StreamController(ILogger<StreamController> logger)
    {
        _logger = logger;
    }

    [HttpGet("stream/{login}")]
    public async Task GetStream(string login)
    {
        _logger.LogInformation("Starting stream for {Login}", login);
        var sw = Stopwatch.StartNew();

        var url = $"twitch.tv/{login}";

        // Start streamlink
        // Note: specifying 720p60,720p,best can sometimes speed up the selection process
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "streamlink",
            Arguments = $"{url} best --stdout --quiet --twitch-disable-ads --twitch-disable-hosting --twitch-disable-reruns --hls-live-edge 3 --stream-segment-threads 2 --hls-segment-ignore-redirect",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = processStartInfo };

        try
        {
            process.Start();

            var stdout = process.StandardOutput.BaseStream;
            var burstBuffer = new byte[131072]; // 128KB burst to prime the pump
            
            _logger.LogDebug("[{Login}] Probing stream...", login);

            // Wait until we have a healthy burst of data
            // This masks the "probe" time and prevents the stream from starting empty
            int totalBytesRead = 0;
            while (totalBytesRead < burstBuffer.Length)
            {
                int read = await stdout.ReadAsync(burstBuffer.AsMemory(totalBytesRead, burstBuffer.Length - totalBytesRead), HttpContext.RequestAborted);
                if (read <= 0) break;
                totalBytesRead += read;
            }
            
            if (totalBytesRead <= 0)
            {
                _logger.LogWarning("[{Login}] Streamlink produced no data.", login);
                Response.StatusCode = 500;
                return;
            }

            _logger.LogInformation("[{Login}] Stream burst ready ({Bytes} bytes) in {Elapsed}ms.", login, totalBytesRead, sw.ElapsedMilliseconds);

            // 1. Send Headers ONLY when we actually have data to send
            Response.ContentType = "video/mp2t"; 
            Response.Headers["Cache-Control"] = "no-cache";
            
            var responseBodyFeature = HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
            if (responseBodyFeature != null)
            {
                responseBodyFeature.DisableBuffering();
            }

            // Write the burst
            await Response.Body.WriteAsync(burstBuffer.AsMemory(0, totalBytesRead), HttpContext.RequestAborted);
            
            // Continue streaming everything else
            await stdout.CopyToAsync(Response.Body, HttpContext.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Stream for {Login} was cancelled by client.", login);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error streaming {Login}", login);
            if (!Response.HasStarted)
            {
                Response.StatusCode = 500;
            }
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch (Exception) { /* Process might not have started */ }
        }
    }
}
