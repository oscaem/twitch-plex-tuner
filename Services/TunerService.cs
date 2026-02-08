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

    public object GetDiscover()
    {
        return new
        {
            FriendlyName = "Twitch Tuner",
            Manufacturer = "TwitchPlexTuner",
            ModelNumber = "TPT-01",
            FirmwareName = "tpt_firmware",
            FirmwareVersion = "20240101",
            TunerCount = 5,
            DeviceID = "TPT12345",
            DeviceAuth = "none",
            BaseURL = _config.BaseUrl,
            LineupURL = $"{_config.BaseUrl}/lineup.json"
        };
    }

    public List<object> GetLineup()
    {
        return _twitchService.GetChannels().Select((c, i) => new
        {
            GuideName = c.DisplayName,
            GuideNumber = (i + 1).ToString(),
            URL = $"{_config.BaseUrl}/stream/{c.Login}"
        }).Cast<object>().ToList();
    }

    public string GetM3U()
    {
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        var channels = _twitchService.GetChannels();
        for (int i = 0; i < channels.Count; i++)
        {
            var c = channels[i];
            sb.AppendLine($"#EXTINF:-1 tvg-id=\"{c.Login}\" tvg-name=\"{c.DisplayName}\" tvg-logo=\"{c.ProfileImageUrl}\" group-title=\"Twitch\",{c.DisplayName}");
            sb.AppendLine($"{_config.BaseUrl}/stream/{c.Login}");
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

            if (c.IsLive)
            {
                var prog = new XElement("programme",
                    new XAttribute("start", c.CurrentStream!.StartedAt.ToString("yyyyMMddHHmmss +0000")),
                    new XAttribute("stop", DateTime.UtcNow.AddHours(24).ToString("yyyyMMddHHmmss +0000")),
                    new XAttribute("channel", c.Login),
                    new XElement("title", c.CurrentStream.Title),
                    new XElement("desc", $"Playing {c.CurrentStream.GameName}"),
                    new XElement("category", "Live"));
                doc.Root!.Add(prog);
            }
            else
            {
                var prog = new XElement("programme",
                    new XAttribute("start", DateTime.UtcNow.ToString("yyyyMMddHHmmss +0000")),
                    new XAttribute("stop", DateTime.UtcNow.AddHours(24).ToString("yyyyMMddHHmmss +0000")),
                    new XAttribute("channel", c.Login),
                    new XElement("title", $"{c.DisplayName} - Offline"),
                    new XElement("desc", "Streamer is currently offline."),
                    new XElement("category", "Offline"));
                doc.Root!.Add(prog);
            }
        }

        return doc.ToString();
    }
}
