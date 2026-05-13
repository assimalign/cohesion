using System;
using System.Threading;

namespace Assimalign.Cohesion.Logging.Debug.Internal;

/// <summary>
/// Per-category logger created by <see cref="DebugLoggerProvider"/>.
/// </summary>
internal sealed class DebugLogger : ILogger
{
    private readonly string _category;
    private readonly DebugLoggerProvider _provider;

    public DebugLogger(string category, DebugLoggerProvider provider)
    {
        _category = category;
        _provider = provider;
    }

    public bool IsEnabled(LogLevel level) => _provider.IsEnabledFor(level);

    public void Log(ILogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!IsEnabled(entry.Level))
        {
            return;
        }

        _provider.Write(entry);
    }

    public IScopedLogger BeginScope(ILogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Emit the seed so the developer sees scope-open events in the debug stream.
        if (IsEnabled(entry.Level))
        {
            _provider.Write(entry);
        }

        return new DebugScopedLogger(entry.Id, _category, _provider);
    }

    private sealed class DebugScopedLogger : IScopedLogger
    {
        private readonly string _category;
        private readonly DebugLoggerProvider _provider;
        private int _disposed;

        public DebugScopedLogger(LogId parentId, string category, DebugLoggerProvider provider)
        {
            ParentId = parentId;
            _category = category;
            _provider = provider;
        }

        public LogId ParentId { get; }

        public bool IsEnabled(LogLevel level) => Volatile.Read(ref _disposed) == 0 && _provider.IsEnabledFor(level);

        public void Log(ILogEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (!IsEnabled(entry.Level))
            {
                return;
            }
            _provider.Write(entry);
        }

        public IScopedLogger BeginScope(ILogEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);

            if (IsEnabled(entry.Level))
            {
                _provider.Write(entry);
            }

            return new DebugScopedLogger(entry.Id, _category, _provider);
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
        }
    }
}
