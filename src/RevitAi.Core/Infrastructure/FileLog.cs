namespace RevitAi.Core.Infrastructure;

/// <summary>
/// Minimal daily log file. Logging must never take Revit down, so I/O failures are swallowed.
/// Never log secrets (ADR-029).
/// </summary>
public sealed class FileLog
{
    private readonly string _directory;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _lock = new();

    public FileLog(string directory, Func<DateTimeOffset>? now = null)
    {
        _directory = directory;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public string CurrentFilePath => Path.Combine(_directory, $"revitai-{_now():yyyyMMdd}.log");

    public void Info(string message) => Write("INFO", message);

    public void Warning(string message) => Write("WARN", message);

    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    private void Write(string level, string message)
    {
        try
        {
            string line = $"{_now():yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}{Environment.NewLine}";
            lock (_lock)
            {
                Directory.CreateDirectory(_directory);
                File.AppendAllText(CurrentFilePath, line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nowhere left to report this; never let logging crash the host.
        }
    }
}
