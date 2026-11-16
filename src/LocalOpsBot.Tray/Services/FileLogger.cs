using System.IO;
using Microsoft.Extensions.Logging;

namespace LocalOpsBot.Tray.Services;

/// <summary>
/// A tiny append-only file logger (no Serilog dependency in the tray). It exists to make the
/// notification-forwarding pipeline observable: it runs headless in the tray process, so without
/// this its <see cref="ILogger"/> calls (access status, poll failures, blocks) went to a NullLogger
/// and could not be diagnosed. Writes timestamped lines to ProgramData\Homebase\logs, size-capped
/// and rolled by <see cref="RollingLogFile"/>.
/// </summary>
internal sealed class FileLogger<T> : ILogger<T>
{
    private readonly RollingLogFile _file;

    public FileLogger(string path) => _file = RollingLogFile.For(path);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {typeof(T).Name}: {formatter(state, exception)}";
        if (exception is not null) line += Environment.NewLine + exception;
        _file.Append(line);
    }
}
