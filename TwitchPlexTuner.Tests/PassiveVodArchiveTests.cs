using TwitchPlexTuner.Services;

namespace TwitchPlexTuner.Tests;

public class PassiveVodArchiveTests
{
    [Fact]
    public void ArchivedVodId_RemainsRecordedAfterVideoFileIsDeleted()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var archivePath = PassiveVodArchive.GetPath(directory);
            var videoPath = Path.Combine(directory, "video.ts");
            File.WriteAllText(videoPath, "recording");
            PassiveVodArchive.AppendId(archivePath, "vod-123");

            File.Delete(videoPath);

            Assert.Contains("vod-123", PassiveVodArchive.ReadIds(archivePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
