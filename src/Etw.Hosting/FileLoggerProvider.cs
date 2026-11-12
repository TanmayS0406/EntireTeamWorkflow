using System.Text;
using Etw.Shared;
using Microsoft.Extensions.Logging;

namespace Etw.Hosting;

/// <summary>Minimal daily-rolling file logger so a Windows service can be diagnosed without attaching a debugger.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _service;
    private readonly object _gate = new();

    public FileLoggerProvider(string service)
    {
        _service = service;
        try
        {
            foreach (var old in Directory.GetFiles(EtwPaths.Logs, service + "-*.log").Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-14)))
            {
                File.Delete(old);
            }
        }
        catch (IOException)
        {
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    public void Dispose()
    {
    }

    private void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                var path = Path.Combine(EtwPaths.Logs, $"{_service}-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
                // Logging must never take the service down.
            }
        }
    }

    private sealed class FileLogger(string category, FileLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information
            && !(category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) && logLevel < LogLevel.Warning);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var text = formatter(state, exception);
            owner.Write($"{DateTime.Now:HH:mm:ss.fff} [{logLevel.ToString()[..3].ToUpperInvariant()}] {category}: {text}{(exception is null ? "" : Environment.NewLine + exception)}");
        }
    }
}
