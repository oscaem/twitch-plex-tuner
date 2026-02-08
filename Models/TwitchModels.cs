using System;
using System.Collections.Generic;

namespace TwitchPlexTuner.Models;

using System.Text.Json.Serialization;

public class TwitchConfig
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string SubscriptionsPath { get; set; } = "/config/subscriptions.yaml";
    public string BaseUrl { get; set; } = "http://localhost:5000";
}

public class TwitchUser
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
    [JsonPropertyName("login")]
    public string Login { get; set; } = string.Empty;
    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("profile_image_url")]
    public string ProfileImageUrl { get; set; } = string.Empty;
}

public class TwitchStream
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
    [JsonPropertyName("user_id")]
    public string UserId { get; set; } = string.Empty;
    [JsonPropertyName("user_login")]
    public string UserLogin { get; set; } = string.Empty;
    [JsonPropertyName("user_name")]
    public string UserName { get; set; } = string.Empty;
    [JsonPropertyName("game_id")]
    public string GameId { get; set; } = string.Empty;
    [JsonPropertyName("game_name")]
    public string GameName { get; set; } = string.Empty;
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;
    [JsonPropertyName("thumbnail_url")]
    public string ThumbnailUrl { get; set; } = string.Empty;
    [JsonPropertyName("started_at")]
    public DateTime StartedAt { get; set; }
}

public class TwitchResponse<T>
{
    [JsonPropertyName("data")]
    public List<T> Data { get; set; } = new();
}

public class TwitchTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;
    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }
    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = string.Empty;
}

public class ChannelInfo
{
    public string Login { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ProfileImageUrl { get; set; } = string.Empty;
    public bool IsLive { get; set; }
    public string StreamTitle { get; set; } = string.Empty;
    public string GameName { get; set; } = string.Empty;
    public string StreamThumbnailUrl { get; set; } = string.Empty;
    public DateTime? StartedAt { get; set; }
}
