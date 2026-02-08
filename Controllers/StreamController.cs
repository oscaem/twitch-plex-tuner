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
            Arguments = $"{url} best --stdout --quiet --twitch-disable-ads --twitch-low-latency --twitch-disable-hosting --twitch-disable-reruns --hls-live-edge 1 --stream-segment-threads 2",
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
            var initialBuffer = new byte[1]; 
            
            // Wait for exactly ONE byte to prove the stream is valid
            // 64KB was taking 5 seconds to fill, which is too slow for Plex.
            int bytesRead = await stdout.ReadAsync(initialBuffer, 0, 1, HttpContext.RequestAborted);
            
            if (bytesRead <= 0)
            {
                // Give it a tiny bit of time to exit so we can catch the error message
                await Task.Delay(500);
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

            _logger.LogInformation("[{Login}] First byte received in {Elapsed}ms. Starting stream.", login, sw.ElapsedMilliseconds);

            Response.ContentType = "video/mp2t"; 
            Response.Headers["Cache-Control"] = "no-cache";
            
            var responseBodyFeature = HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
            if (responseBodyFeature != null)
            {
                responseBodyFeature.DisableBuffering();
            }

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
