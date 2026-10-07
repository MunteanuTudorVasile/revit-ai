using RevitAi.Core.Infrastructure;

namespace RevitAi.Core.Tests.Infrastructure;

public sealed class FileLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "revitai-tests-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Writes_levelled_lines_to_a_daily_file()
    {
        var now = new DateTimeOffset(2026, 10, 7, 20, 30, 0, TimeSpan.Zero);
        var log = new FileLog(_dir, () => now);

        log.Info("started");
        log.Error("failed", new InvalidOperationException("boom"));

        string path = Path.Combine(_dir, "revitai-20261007.log");
        Assert.Equal(path, log.CurrentFilePath);
        string content = File.ReadAllText(path);
        Assert.Contains("[INFO] started", content);
        Assert.Contains("[ERROR] failed", content);
        Assert.Contains("boom", content);
    }

    [Fact]
    public void Unwritable_location_does_not_throw()
    {
        string blocker = Path.Combine(_dir, "file");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(blocker, "");

        // The log directory path points at a file, so creating it fails.
        var log = new FileLog(Path.Combine(blocker, "logs"));

        log.Info("ignored");
    }
}
