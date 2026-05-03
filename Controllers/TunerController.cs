using Microsoft.AspNetCore.Mvc;
using TwitchPlexTuner.Services;

namespace TwitchPlexTuner.Controllers;

[ApiController]
public class TunerController : ControllerBase
{
    private static readonly TimeSpan TunerRequestRefreshAge = TimeSpan.FromMinutes(2);
    private readonly TunerService _tunerService;
    private readonly TwitchService _twitchService;

    public TunerController(TunerService tunerService, TwitchService twitchService)
    {
        _tunerService = tunerService;
        _twitchService = twitchService;
    }

    [HttpGet("discover.json")]
    public IActionResult Discover()
    {
        SetNoCacheHeaders();
        // Use configured BaseUrl if set, otherwise fallback to request host
        return Ok(_tunerService.GetDiscover());
    }

    [HttpGet("lineup_status.json")]
    public IActionResult LineupStatus()
    {
        SetNoCacheHeaders();
        return Ok(new { ScanInProgress = 0, ScanPossible = 1, Source = "Cable", SourceList = new[] { "Cable" } });
    }

    [HttpGet("lineup.json")]
    public async Task<IActionResult> Lineup()
    {
        SetNoCacheHeaders();
        await _twitchService.EnsureChannelsFreshAsync(TunerRequestRefreshAge);
        return Ok(_tunerService.GetLineup());
    }

    [HttpGet("playlist.m3u")]
    public async Task<IActionResult> Playlist()
    {
        try
        {
            SetNoCacheHeaders();
            await _twitchService.EnsureChannelsFreshAsync(TunerRequestRefreshAge);
            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            return Content(_tunerService.GetM3U(baseUrl), "text/plain");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error generating M3U: {ex}");
            return StatusCode(500, ex.Message);
        }
    }

    [HttpGet("epg.xml")]
    public async Task<IActionResult> Epg()
    {
        try
        {
            SetNoCacheHeaders();
            await _twitchService.EnsureChannelsFreshAsync(TunerRequestRefreshAge);
            return Content(_tunerService.GetXMLTV(), "text/xml");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error generating XMLTV: {ex}");
            return StatusCode(500, ex.Message);
        }
    }

    [HttpGet("update")]
    public async Task<IActionResult> Update()
    {
        SetNoCacheHeaders();
        await _twitchService.UpdateChannelsAsync();
        return Ok("Updated");
    }

    private void SetNoCacheHeaders()
    {
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";
    }
}
