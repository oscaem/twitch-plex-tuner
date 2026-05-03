using System.Diagnostics;

namespace TwitchPlexTuner.Services;

internal static class StreamlinkArguments
{
    public static void AddTwitchAdFiltering(ProcessStartInfo psi)
    {
        psi.ArgumentList.Add("--twitch-disable-ads");
    }

    public static void AddTwitchOAuthHeader(ProcessStartInfo psi, string? twitchOAuthToken)
    {
        var headerValue = BuildOAuthHeaderValue(twitchOAuthToken);
        if (headerValue is null) return;

        psi.ArgumentList.Add($"--twitch-api-header=Authorization={headerValue}");
    }

    public static string ToSafeLogString(ProcessStartInfo psi)
    {
        return string.Join(" ", psi.ArgumentList.Select(RedactAndQuote));
    }

    private static string? BuildOAuthHeaderValue(string? twitchOAuthToken)
    {
        if (string.IsNullOrWhiteSpace(twitchOAuthToken)) return null;

        var token = twitchOAuthToken.Trim();
        return token.StartsWith("OAuth ", StringComparison.OrdinalIgnoreCase)
            ? token
            : $"OAuth {token}";
    }

    private static string RedactAndQuote(string arg)
    {
        var safeArg = arg.StartsWith("--twitch-api-header=Authorization=OAuth ", StringComparison.OrdinalIgnoreCase)
            ? "--twitch-api-header=Authorization=OAuth ***"
            : arg;

        return safeArg.Any(char.IsWhiteSpace)
            ? $"\"{safeArg.Replace("\"", "\\\"")}\""
            : safeArg;
    }
}
