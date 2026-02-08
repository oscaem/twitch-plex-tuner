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

        // 2. Start actual stream
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "streamlink",
            Arguments = $"{url} best --stdout --quiet --twitch-disable-ads --twitch-low-latency --hls-live-edge 1 --stream-segment-threads 3",
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
            var buffer = new byte[65536]; // Read 64KB to prime FFmpeg/Threadfin instantly
            
            _logger.LogDebug("[{Login}] Waiting for streamlink data...", login);
            int bytesRead = await stdout.ReadAsync(buffer, 0, buffer.Length, HttpContext.RequestAborted);
            _logger.LogInformation("[{Login}] Received {Bytes} bytes in {Elapsed}ms", login, bytesRead, sw.ElapsedMilliseconds);
            
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

            Response.ContentType = "video/mp2t"; // MPEG-TS is best for Plex
            Response.Headers["Cache-Control"] = "no-cache";
            
            var responseBodyFeature = HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
            if (responseBodyFeature != null)
            {
                responseBodyFeature.DisableBuffering();
            }

            // Write the first chunk we already read
            await Response.Body.WriteAsync(buffer.AsMemory(0, bytesRead), HttpContext.RequestAborted);
            _logger.LogInformation("[{Login}] Headers and first chunk sent. Total startup: {Elapsed}ms", login, sw.ElapsedMilliseconds);
            
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
