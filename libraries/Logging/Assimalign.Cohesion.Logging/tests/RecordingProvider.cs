using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Assimalign.Cohesion.Logging.Tests;

/// <summary>
/// In-memory logger provider that records every entry it receives. Useful for assertions in
/// the composite / scope / enrichment tests.
/// </summary>
internal sealed class RecordingProvider : ILoggerProvider
{
    public RecordingProvider(string name = "Recording", bool throwOnLog = false, bool throwOnScope = false)
    {
        Name = name;
        ThrowOnLog = throwOnLog;
        ThrowOnScope = throwOnScope;
    }

    public string Name { get; }
    public ConcurrentBag<ILogEntry> Entries { get; } = new();
    public int DisposeCount;
    public bool ThrowOnLog { get; }
    public bool ThrowOnScope { get; }
    public bool IsDisposed => Volatile.Read(ref DisposeCount) > 0;

    public ILogger Create(string category) => new RecordingLogger(category, this);

    public void Dispose() => Interlocked.Increment(ref DisposeCount);

    private sealed class RecordingLogger : ILogger
    {
        private readonly string _category;
        private readonly RecordingProvider _owner;

        public RecordingLogger(string category, RecordingProvider owner)
        {
            _category = category;
            _owner = owner;
        }

        public bool IsEnabled(LogLevel level) => level != LogLevel.None && !_owner.IsDisposed;

        public void Log(ILogEntry entry)
        {
            if (_owner.ThrowOnLog)
            {
                throw new System.InvalidOperationException("provider rejected entry");
            }
            _owner.Entries.Add(entry);
        }

        public IScopedLogger BeginScope(ILogEntry entry)
        {
            if (_owner.ThrowOnScope)
            {
                throw new System.InvalidOperationException("provider rejected scope");
            }
            _owner.Entries.Add(entry);
            return new RecordingScope(entry.Id, _category, _owner);
        }
    }

    private sealed class RecordingScope : IScopedLogger
    {
        private readonly string _category;
        private readonly RecordingProvider _owner;
        private int _disposed;

        public RecordingScope(LogId parentId, string category, RecordingProvider owner)
        {
            ParentId = parentId;
            _category = category;
            _owner = owner;
        }

        public LogId ParentId { get; }

        public bool IsEnabled(LogLevel level) => Volatile.Read(ref _disposed) == 0 && level != LogLevel.None;

        public void Log(ILogEntry entry) => _owner.Entries.Add(entry);

        public IScopedLogger BeginScope(ILogEntry entry)
        {
            _owner.Entries.Add(entry);
            return new RecordingScope(entry.Id, _category, _owner);
        }

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }
}
