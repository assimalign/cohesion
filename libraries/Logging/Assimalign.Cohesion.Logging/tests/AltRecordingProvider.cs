using System.Collections.Concurrent;
using System.Threading;

namespace Assimalign.Cohesion.Logging.Tests;

/// <summary>
/// Distinct runtime type used by tests that exercise provider-type-specific filter rules. It
/// records the same way <see cref="RecordingProvider"/> does, but has its own
/// <see cref="System.Type"/> identity so a rule with <c>ProviderType = typeof(RecordingProvider)</c>
/// does not apply to it.
/// </summary>
internal sealed class AltRecordingProvider : ILoggerProvider
{
    public AltRecordingProvider(string name = "AltRecording")
    {
        Name = name;
    }

    public string Name { get; }
    public ConcurrentBag<ILoggerEntry> Entries { get; } = new();
    public int DisposeCount;
    public bool IsDisposed => Volatile.Read(ref DisposeCount) > 0;

    public ILogger Create(string category) => new AltLogger(category, this);

    public void Dispose() => Interlocked.Increment(ref DisposeCount);

    private sealed class AltLogger : ILogger
    {
        private readonly string _category;
        private readonly AltRecordingProvider _owner;

        public AltLogger(string category, AltRecordingProvider owner)
        {
            _category = category;
            _owner = owner;
        }

        public bool IsEnabled(LogLevel level) => level != LogLevel.None && !_owner.IsDisposed;

        public void Log(ILoggerEntry entry) => _owner.Entries.Add(entry);

        public IScopedLogger BeginScope(ILoggerEntry entry)
        {
            _owner.Entries.Add(entry);
            return new AltScope(entry.Id);
        }

        private sealed class AltScope : IScopedLogger
        {
            public AltScope(LogId parentId) { ParentId = parentId; }
            public LogId ParentId { get; }
            public bool IsEnabled(LogLevel level) => false;
            public void Log(ILoggerEntry entry) { }
            public IScopedLogger BeginScope(ILoggerEntry entry) => this;
            public void Dispose() { }
        }
    }
}
