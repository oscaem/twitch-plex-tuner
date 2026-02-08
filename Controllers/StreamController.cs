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

        var url = $"twitch.tv/{login}";

        // 1. Direct Stream Launch (Optimized for speed)
        // We skp the pre-check because it adds 2s latency which causes Plex to timeout.
        // If the stream is offline, the main process below will exit immediately and return 500.

        // 2. Start actual stream
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "streamlink",
            Arguments = $"{url} best --stdout --quiet --twitch-disable-ads --hls-live-edge 2 --hls-segment-threads 4",
            RedirectStandardOutput = true,
            RedirectStandardError = true, // Capture error
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = processStartInfo };

        try
        {
            process.Start();

            var stdout = process.StandardOutput.BaseStream;
            var buffer = new byte[4096]; // Read a decent chunk to be sure
            
            // Wait for data with a timeout (implied by HttpContext.RequestAborted or we can add a Task.Delay)
            int bytesRead = await stdout.ReadAsync(buffer, 0, buffer.Length, HttpContext.RequestAborted);
            
            if (bytesRead <= 0)
            {
                if (process.HasExited && process.ExitCode != 0)
                {
                    var error = await process.StandardError.ReadToEndAsync();
                    _logger.LogError("Streamlink failed for {Login}: {Error}", login, error);
                }
                else
                {
                    _logger.LogWarning("Streamlink produced no data for {Login}", login);
                }
                
                Response.StatusCode = 500;
                return;
            }

            _logger.LogInformation("Streaming data started for {Login} ({Bytes} bytes in first read)", login, bytesRead);

            Response.ContentType = "video/mp2t"; // MPEG-TS is best for Plex
            Response.Headers["Cache-Control"] = "no-cache";
            
            var responseBodyFeature = HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
            if (responseBodyFeature != null)
            {
                responseBodyFeature.DisableBuffering();
            }

            // Write the first chunk we already read
            await Response.Body.WriteAsync(buffer.AsMemory(0, bytesRead), HttpContext.RequestAborted);
            
            // Continue streaming
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
