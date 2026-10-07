using RevitAi.Core.Infrastructure;

namespace RevitAi.Core.Tests.Infrastructure;

public sealed class AddinSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "revitai-tests-" + Guid.NewGuid());

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Missing_file_is_created_with_defaults()
    {
        AddinSettings settings = AddinSettings.Load(SettingsPath, out string? problem);

        Assert.Null(problem);
        Assert.Equal(30, settings.DispatcherTimeoutSeconds);
        Assert.True(File.Exists(SettingsPath));
    }

    [Fact]
    public void Existing_file_is_read()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """{ "DispatcherTimeoutSeconds": 12 }""");

        AddinSettings settings = AddinSettings.Load(SettingsPath, out string? problem);

        Assert.Null(problem);
        Assert.Equal(12, settings.DispatcherTimeoutSeconds);
    }

    [Fact]
    public void Malformed_file_falls_back_to_defaults_and_reports_problem()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ not json");

        AddinSettings settings = AddinSettings.Load(SettingsPath, out string? problem);

        Assert.NotNull(problem);
        Assert.Equal(30, settings.DispatcherTimeoutSeconds);
    }

    [Fact]
    public void Non_positive_timeout_is_replaced_by_default()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """{ "DispatcherTimeoutSeconds": 0 }""");

        AddinSettings settings = AddinSettings.Load(SettingsPath, out string? problem);

        Assert.NotNull(problem);
        Assert.Equal(30, settings.DispatcherTimeoutSeconds);
    }
}
