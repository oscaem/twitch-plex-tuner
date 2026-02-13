using TwitchPlexTuner.Models;
using TwitchPlexTuner.Services;

var builder = WebApplication.CreateBuilder(args);

// Configuration from Environment Variables
builder.Services.Configure<TwitchConfig>(options =>
{
    options.ClientId = Environment.GetEnvironmentVariable("CLIENT_ID") ?? string.Empty;
    options.ClientSecret = Environment.GetEnvironmentVariable("CLIENT_SECRET") ?? string.Empty;
    options.SubscriptionsPath = Environment.GetEnvironmentVariable("SUBSCRIPTIONS_PATH") ?? "/config/subscriptions.yaml";
    options.BaseUrl = Environment.GetEnvironmentVariable("BASE_URL") ?? "http://localhost:5000";
    options.PlexServerUrl = Environment.GetEnvironmentVariable("PLEX_SERVER_URL") ?? string.Empty;
    options.PlexToken = Environment.GetEnvironmentVariable("PLEX_TOKEN") ?? string.Empty;
    options.ThreadfinUrl = Environment.GetEnvironmentVariable("THREADFIN_URL") ?? string.Empty;
    options.StreamEngine = Environment.GetEnvironmentVariable("STREAM_ENGINE") ?? "streamlink";
});

// Add services
builder.Services.AddSingleton<TwitchService>();
builder.Services.AddSingleton<TunerService>();
builder.Services.AddHostedService<TwitchUpdateService>();
builder.Services.AddHostedService<RecordingService>();
builder.Services.AddHostedService<PlexService>();
builder.Services.AddHttpClient();
builder.Services.AddControllers();

var app = builder.Build();

// Ensure channels are updated on startup
using (var scope = app.Services.CreateScope())
{
    var twitchService = scope.ServiceProvider.GetRequiredService<TwitchService>();
    await twitchService.UpdateChannelsAsync();
}

app.MapControllers();

app.Run();
