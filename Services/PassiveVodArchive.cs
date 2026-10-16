namespace TwitchPlexTuner.Services;

internal static class PassiveVodArchive
{
    public const string FileName = ".twitch-vod-archive.txt";

    public static string GetPath(string streamerDirectory) =>
        Path.Combine(streamerDirectory, FileName);

    public static HashSet<string> ReadIds(string archivePath)
    {
        if (!File.Exists(archivePath))
            return new HashSet<string>(StringComparer.Ordinal);

        return File.ReadLines(archivePath)
            .Select(line => line.Trim())
            .Where(id => !string.IsNullOrEmpty(id))
            .ToHashSet(StringComparer.Ordinal);
    }

    public static void AppendId(string archivePath, string videoId)
    {
        if (string.IsNullOrWhiteSpace(videoId) || videoId.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            throw new ArgumentException("A Twitch VOD ID must be a non-empty single line.", nameof(videoId));

        if (ReadIds(archivePath).Contains(videoId))
            return;

        File.AppendAllText(archivePath, videoId + Environment.NewLine);
    }
}
