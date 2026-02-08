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

        // 1. Check if live using streamlink (simpler than API for now)
        // This avoids starting a full stream process if offline
        // Note: In a production env, caching this status would be better
        var checkProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "streamlink",
                Arguments = $"{url} best --stream-url", // This just gets the URL, doesn't stream
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        checkProcess.Start();
        await checkProcess.WaitForExitAsync();

        if (checkProcess.ExitCode != 0)
        {
            var error = await checkProcess.StandardError.ReadToEndAsync();
            _logger.LogWarning("Streamlink check failed for {Login}: {Error}", login, error);
            Response.StatusCode = 404; // Not Found (or offline)
            return;
        }

        // 2. Start actual stream
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "streamlink",
            Arguments = $"{url} best --stdout --quiet --twitch-disable-ads",
            RedirectStandardOutput = true,
            RedirectStandardError = true, // Capture error
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = processStartInfo };

        try
        {
            process.Start();

            // Check if it exits immediately
            if (process.WaitForExit(1000) && process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync();
                _logger.LogError("Streamlink failed immediately for {Login}: {Error}", login, error);
                Response.StatusCode = 500;
                return;
            }

            Response.ContentType = "video/mp2t"; // MPEG-TS is best for Plex
            Response.Headers["Cache-Control"] = "no-cache";

            await process.StandardOutput.BaseStream.CopyToAsync(Response.Body, HttpContext.RequestAborted);
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
