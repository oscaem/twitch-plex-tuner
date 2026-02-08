using Microsoft.AspNetCore.Mvc;
using TwitchPlexTuner.Services;

namespace TwitchPlexTuner.Controllers;

[ApiController]
public class TunerController : ControllerBase
{
    private readonly TunerService _tunerService;
    private readonly TwitchService _twitchService;

    public TunerController(TunerService tunerService, TwitchService twitchService)
    {
        _tunerService = tunerService;
        _twitchService = twitchService;
    }

    [HttpGet("discover.json")]
    public IActionResult Discover() => Ok(_tunerService.GetDiscover());

    [HttpGet("lineup_status.json")]
    public IActionResult LineupStatus() => Ok(new { ScanInProgress = 0, ScanPossible = 1, Source = "Cable", SourceList = new[] { "Cable" } });

    [HttpGet("lineup.json")]
    public IActionResult Lineup() => Ok(_tunerService.GetLineup());

    [HttpGet("playlist.m3u")]
    public IActionResult Playlist()
    {
        try
        {
            return Content(_tunerService.GetM3U(), "text/plain");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error generating M3U: {ex}");
            return StatusCode(500, ex.Message);
        }
    }

    [HttpGet("epg.xml")]
    public IActionResult Epg()
    {
        try
        {
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
        await _twitchService.UpdateChannelsAsync();
        return Ok("Updated");
    }
}
