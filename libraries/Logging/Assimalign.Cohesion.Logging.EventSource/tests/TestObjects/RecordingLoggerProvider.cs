using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.Logging.Tests;

/// <summary>
/// A provider that records every entry it receives, optionally running a callback inside the write.
/// </summary>
internal sealed class RecordingLoggerProvider : LoggerProvider
{
    private readonly ConcurrentQueue<ILoggerEntry> _entries = new();
    private readonly Action<ILoggerEntry>? _onLog;

    public RecordingLoggerProvider(Action<ILoggerEntry>? onLog = null)
    {
        _onLog = onLog;
    }

    /// <inheritdoc />
    public override string Name => "Recording";

    /// <summary>The entries written to <paramref name="category"/>, in arrival order.</summary>
    public IReadOnlyList<ILoggerEntry> EntriesFor(string category)
        => _entries.Where(entry => string.Equals(entry.Category, category, StringComparison.Ordinal)).ToArray();

    /// <inheritdoc />
    protected override Logger CreateCore(string category) => new RecordingLogger(category, this);

    private void Record(ILoggerEntry entry)
    {
        _entries.Enqueue(entry);
        _onLog?.Invoke(entry);
    }

    private sealed class RecordingLogger : Logger
    {
        private readonly RecordingLoggerProvider _provider;

        public RecordingLogger(string category, RecordingLoggerProvider provider)
            : base(category)
        {
            _provider = provider;
        }

        public override bool IsEnabled(LogLevel level) => base.IsEnabled(level) && !_provider.IsDisposed;

        protected override void WriteCore(ILoggerEntry entry) => _provider.Record(entry);

        protected override IScopedLogger BeginScopeCore(ILoggerEntry entry)
            => throw new NotSupportedException("The forwarder never opens scopes.");
    }
}
