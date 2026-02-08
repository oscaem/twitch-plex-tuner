using System;
using System.Collections.Generic;

namespace TwitchPlexTuner.Models;

public class TwitchConfig
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string SubscriptionsPath { get; set; } = "/config/subscriptions.yaml";
    public string BaseUrl { get; set; } = "http://localhost:5000";
}

public class TwitchUser
{
    public string Id { get; set; } = string.Empty;
    public string Login { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ProfileImageUrl { get; set; } = string.Empty;
}

public class TwitchStream
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string UserLogin { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string GameId { get; set; } = string.Empty;
    public string GameName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
}

public class TwitchResponse<T>
{
    public List<T> Data { get; set; } = new();
}

public class ChannelInfo
{
    public string Login { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ProfileImageUrl { get; set; } = string.Empty;
}
