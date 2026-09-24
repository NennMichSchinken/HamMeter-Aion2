using Microsoft.Extensions.Logging;

namespace HamMeter;

// Minimal file logger (HamMeter.log next to the config) so problems in the field can be
// diagnosed without a console window.
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter m_writer;
    private readonly Lock m_sync = new();
    private readonly LogLevel m_minLevel;

    public FileLoggerProvider(string path, LogLevel minLevel)
    {
        m_minLevel = minLevel;
        m_writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true,
        };
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (m_sync)
        {
            m_writer.Dispose();
        }
    }

    private void Write(LogLevel level, string category, string message, Exception? ex)
    {
        lock (m_sync)
        {
            try
            {
                m_writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{level}] {category}: {message}");
                if (ex is not null)
                {
                    m_writer.WriteLine(ex);
                }
            }
            catch (ObjectDisposedException)
            {
                // Late log calls during shutdown.
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= owner.m_minLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (this.IsEnabled(logLevel))
            {
                owner.Write(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}
