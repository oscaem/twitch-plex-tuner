using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using Moq;
using TwitchPlexTuner.Models;
using TwitchPlexTuner.Services;
using Xunit;

namespace TwitchPlexTuner.Tests;

public class TunerServiceTests
{
    private readonly Mock<IOptions<TwitchConfig>> _mockConfig;
    private readonly Mock<TwitchService> _mockTwitchService;
    private readonly TunerService _tunerService;

    public TunerServiceTests()
    {
        _mockConfig = new Mock<IOptions<TwitchConfig>>();
        _mockConfig.Setup(c => c.Value).Returns(new TwitchConfig { BaseUrl = "http://test-url" });

        _mockTwitchService = new Mock<TwitchService>(_mockConfig.Object);
        _mockTwitchService.Setup(s => s.GetChannels()).Returns(new List<ChannelInfo>
        {
            new ChannelInfo { Login = "channel1", DisplayName = "Channel 1", ProfileImageUrl = "img1.png" },
            new ChannelInfo { Login = "channel2", DisplayName = "Channel 2", ProfileImageUrl = "img2.png" }
        });

        _tunerService = new TunerService(_mockConfig.Object, _mockTwitchService.Object);
    }

    [Fact]
    public void GetM3U_ReturnsValidPlaylist()
    {
        var m3u = _tunerService.GetM3U();

        Assert.Contains("#EXTM3U", m3u);
        Assert.Contains("tvg-id=\"channel1\"", m3u);
        Assert.Contains("http://test-url/stream/channel1", m3u);
        Assert.Contains("tvg-id=\"channel2\"", m3u);
    }

    [Fact]
    public void GetXMLTV_ReturnsValidXmlOrObjects()
    {
        var xml = _tunerService.GetXMLTV();
        var doc = XDocument.Parse(xml);

        var channels = doc.Descendants("channel").ToList();
        Assert.Equal(2, channels.Count);
        Assert.Equal("channel1", channels[0].Attribute("id")?.Value);

        var programmes = doc.Descendants("programme").ToList();
        Assert.Equal(2, programmes.Count);

        // check start time is in the past to ensure "now" is covered
        var startStr = programmes[0].Attribute("start")?.Value; // format "yyyyMMddHHmmss +0000"
        var startTime = DateTime.ParseExact(startStr, "yyyyMMddHHmmss zzz", System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(startTime < DateTime.UtcNow, "Program start time should be in the past");
    }
}
