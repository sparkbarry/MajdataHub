using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RhythmHub.Launcher.Services;

/// <summary>Minimal JSON-lines file logger: one file per day under logs/.</summary>
public sealed class FileLoggerFactory : ILoggerFactory
{
    private readonly StreamWriter _writer;
    private readonly Lock _lock = new();

    public FileLoggerFactory(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        LogDirectory = logDirectory;
        var path = Path.Combine(logDirectory, $"launcher-{DateTime.Now:yyyyMMdd}.log");
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }

    public string LogDirectory { get; }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();

    public void Dispose() => _writer.Dispose();

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var line = JsonSerializer.Serialize(new
        {
            ts = DateTimeOffset.Now,
            level = level.ToString(),
            category,
            message,
            exception = exception?.ToString(),
        });
        lock (_lock) _writer.WriteLine(line);
    }

    private sealed class FileLogger(FileLoggerFactory owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) owner.Write(category, logLevel, formatter(state, exception), exception);
        }
    }
}
