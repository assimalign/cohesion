using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Assimalign.Cohesion.Logging.Console.Internal;

/// <summary>
/// Per-category logger created by <see cref="ConsoleLoggerProvider"/>.
/// </summary>
internal sealed class ConsoleLogger : ILogger
{
    private readonly string _category;
    private readonly ConsoleLoggerProvider _provider;

    public ConsoleLogger(string category, ConsoleLoggerProvider provider)
    {
        _category = category;
        _provider = provider;
    }

    public bool IsEnabled(LogLevel level) => level != LogLevel.None && !_provider.IsDisposed;

    public void Log(ILoggerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!IsEnabled(entry.Level))
        {
            return;
        }

        _provider.Write(entry);
    }

    public IScopedLogger BeginScope(ILoggerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Emit the seed so the developer sees scope-open events in the console stream.
        if (IsEnabled(entry.Level))
        {
            _provider.Write(entry);
        }

        return new ConsoleScopedLogger(entry.Id, _category, _provider);
    }

    private sealed class ConsoleScopedLogger : IScopedLogger
    {
        private readonly string _category;
        private readonly ConsoleLoggerProvider _provider;
        private int _disposed;

        public ConsoleScopedLogger(LogId parentId, string category, ConsoleLoggerProvider provider)
        {
            ParentId = parentId;
            _category = category;
            _provider = provider;
        }

        public LogId ParentId { get; }

        public bool IsEnabled(LogLevel level) => Volatile.Read(ref _disposed) == 0 && level != LogLevel.None && !_provider.IsDisposed;

        public void Log(ILoggerEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (!IsEnabled(entry.Level))
            {
                return;
            }
            _provider.Write(entry);
        }

        public IScopedLogger BeginScope(ILoggerEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);

            if (IsEnabled(entry.Level))
            {
                _provider.Write(entry);
            }

            return new ConsoleScopedLogger(entry.Id, _category, _provider);
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
        }
    }
}
