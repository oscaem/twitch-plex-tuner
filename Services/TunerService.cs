using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using TwitchPlexTuner.Models;

namespace TwitchPlexTuner.Services;

public class TunerService
{
    private readonly TwitchConfig _config;
    private readonly TwitchService _twitchService;

    public TunerService(IOptions<TwitchConfig> config, TwitchService twitchService)
    {
        _config = config.Value;
        _twitchService = twitchService;
    }

    public object GetDiscover(string? baseUrl = null)
    {
        var effectiveBaseUrl = baseUrl ?? _config.BaseUrl;
        return new
        {
            FriendlyName = "Twitch Tuner",
            Manufacturer = "TwitchPlexTuner",
            ModelNumber = "TPT-01",
            FirmwareName = "tpt_firmware",
            FirmwareVersion = "20240101",
            TunerCount = 5,
            DeviceID = "TN123456", // Must be 8 chars, seemingly valid for Plex
            DeviceAuth = "none",
            BaseURL = effectiveBaseUrl,
            LineupURL = $"{effectiveBaseUrl}/lineup.json"
        };
    }

    public List<object> GetLineup(string? baseUrl = null)
    {
        var effectiveBaseUrl = baseUrl ?? _config.BaseUrl;
        return _twitchService.GetChannels().Select((c, i) => new
        {
            GuideName = c.DisplayName,
            GuideNumber = (i + 1).ToString(),
            URL = $"{effectiveBaseUrl}/stream/{c.Login}"
        }).Cast<object>().ToList();
    }

    public string GetM3U(string? baseUrl = null)
    {
        var effectiveBaseUrl = baseUrl ?? _config.BaseUrl;
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        var channels = _twitchService.GetChannels();
        for (int i = 0; i < channels.Count; i++)
        {
            var c = channels[i];
            sb.AppendLine($"#EXTINF:-1 tvg-id=\"{c.Login}\" tvg-chno=\"{i + 1}\" tvg-name=\"{c.DisplayName}\" tvg-logo=\"{c.ProfileImageUrl}\" group-title=\"Twitch\",{c.DisplayName}");
            sb.AppendLine($"{effectiveBaseUrl}/stream/{c.Login}");
        }
        return sb.ToString();
    }

    public string GetXMLTV()
    {
        var channels = _twitchService.GetChannels();
        var doc = new XDocument(new XElement("tv"));

        foreach (var c in channels)
        {
            var channelElem = new XElement("channel", new XAttribute("id", c.Login),
                new XElement("display-name", c.DisplayName),
                new XElement("icon", new XAttribute("src", c.ProfileImageUrl)));
            doc.Root!.Add(channelElem);

            // Use live data if available, otherwise generic
            // 1. Past Block (Keep history for buffer)
            var startPast = DateTime.UtcNow.AddHours(-1);
            var endPast = DateTime.UtcNow;
            
            doc.Root!.Add(CreateProgramme(c, startPast, endPast, 
                c.IsLive ? c.StreamTitle : "Offline", 
                "Catch up", "Offline", c.ProfileImageUrl));

            // 2. Current Block (30 mins - Force updates to affect this slot)
            var startCurrent = DateTime.UtcNow;
            var endCurrent = DateTime.UtcNow.AddMinutes(30);
            
            var currentTitle = c.IsLive ? c.StreamTitle : $"{c.DisplayName} is Offline";
            var currentDesc = c.IsLive ? $"Playing {c.GameName}" : "Channel is currently offline";
            var currentCat = c.IsLive ? c.GameName : "Offline";
            var currentIcon = c.IsLive && !string.IsNullOrEmpty(c.StreamThumbnailUrl) ? c.StreamThumbnailUrl : c.ProfileImageUrl;

            doc.Root!.Add(CreateProgramme(c, startCurrent, endCurrent, currentTitle, currentDesc, currentCat, currentIcon));

            // 3. Future Block (Placeholder rest of day)
            var startFuture = endCurrent;
            var endFuture = DateTime.UtcNow.AddHours(24);
            
            doc.Root!.Add(CreateProgramme(c, startFuture, endFuture, 
                $"{c.DisplayName} Offline", 
                "Future programming", "Offline", c.ProfileImageUrl));
        }

        return new XDeclaration("1.0", "utf-8", null) + Environment.NewLine + doc.ToString();
    }

    private XElement CreateProgramme(ChannelInfo c, DateTime start, DateTime end, string title, string desc, string category, string iconUrl)
    {
        return new XElement("programme",
                new XAttribute("start", start.ToString("yyyyMMddHHmmss +0000")),
                new XAttribute("stop", end.ToString("yyyyMMddHHmmss +0000")),
                new XAttribute("channel", c.Login),
                new XElement("title", title),
                new XElement("desc", desc),
                new XElement("category", category),
                new XElement("icon", new XAttribute("src", iconUrl)));
    }
}
