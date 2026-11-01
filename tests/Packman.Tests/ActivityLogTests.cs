using Packman.Services;
using Xunit;

namespace Packman.Tests;

/// <summary>The live console's feed: what reaches it, in what colour, and what is kept out.</summary>
public sealed class ActivityLogTests
{
    [Fact]
    public void UploadLoggerLinesReachTheConsoleAndMetadataDoesNot()
    {
        var log = ActivityLog.Shared;
        log.Clear();

        using (var upload = new UploadLogger("Packman-QA-activity-test"))
        {
            upload.Section("UPLOAD PROCESS");
            upload.Progress(20, "Building .intunewin package");
            upload.LogMetadata("Application Name", "hidden");
            upload.Success("Done");
            upload.Warning("Careful");
            upload.Error("Boom", new InvalidOperationException("bad"));
        }

        Assert.Equal(
            new[]
            {
                (ActivityLevel.Section, "UPLOAD PROCESS"),
                (ActivityLevel.Info, "Building .intunewin package"),
                (ActivityLevel.Success, "Done"),
                (ActivityLevel.Warning, "Careful"),
                (ActivityLevel.Error, "Boom: bad"),
            },
            log.Lines.Select(l => (l.Level, l.Text)));
    }

    [Fact]
    public void SecretsAreRemovedBeforeALineIsShown()
    {
        var log = ActivityLog.Shared;
        log.Clear();

        log.Info("PUT https://stg.blob.core.windows.net/c/f?sv=2023-01-01&sig=AbC%2Fdef%3D failed");
        log.Info("Authorization: Bearer abc.def-123");
        log.Info("client_secret=hunter2 rejected");

        var text = log.Text;
        Assert.Contains("https://stg.blob.core.windows.net/c/f failed", text);
        Assert.DoesNotContain("sig=", text);
        Assert.DoesNotContain("abc.def-123", text);
        Assert.DoesNotContain("hunter2", text);
    }

    [Fact]
    public void TheConsoleKeepsOnlyTheNewestLines()
    {
        var log = ActivityLog.Shared;
        log.Clear();

        for (var i = 0; i < 600; i++) log.Info($"line {i}");

        Assert.Equal(500, log.Lines.Count);
        Assert.Equal("line 100", log.Lines[0].Text);
        Assert.Equal("line 599", log.Lines[^1].Text);
    }
}
