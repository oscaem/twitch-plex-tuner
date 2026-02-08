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
            Arguments = $"{url} 720p60,720p,best --stdout --quiet --twitch-disable-ads --twitch-low-latency --twitch-disable-hosting --twitch-disable-reruns --hls-live-edge 1 --stream-segment-threads 2",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = processStartInfo };

        try
        {
            // 1. Send Headers IMMEDIATELY to satisfy Plex's connection timer
            Response.ContentType = "video/mp2t"; 
            Response.Headers["Cache-Control"] = "no-cache";
            
            var responseBodyFeature = HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
            if (responseBodyFeature != null)
            {
                responseBodyFeature.DisableBuffering();
            }

            // This forces the 200 OK to the client right now
            await Response.Body.FlushAsync(HttpContext.RequestAborted);

            process.Start();

            var stdout = process.StandardOutput.BaseStream;
            var initialBuffer = new byte[1]; 
            
            // Wait for data
            int bytesRead = await stdout.ReadAsync(initialBuffer, 0, 1, HttpContext.RequestAborted);
            
            if (bytesRead <= 0)
            {
                _logger.LogWarning("[{Login}] Streamlink produced no data.", login);
                return;
            }

            _logger.LogInformation("[{Login}] Stream started in {Elapsed}ms.", login, sw.ElapsedMilliseconds);

            // Write that first byte
            await Response.Body.WriteAsync(initialBuffer.AsMemory(0, 1), HttpContext.RequestAborted);
            
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
