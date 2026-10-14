using System.Collections.Concurrent;
using System.Collections.Generic;

using Assimalign.Cohesion.Logging;

namespace Assimalign.Cohesion.Web.Api.Tests.TestObjects;

/// <summary>
/// An in-memory logger provider that records every entry it receives, in arrival order, so a test can
/// assert what the HTTP logging middleware wrote for an exchange.
/// </summary>
internal sealed class RecordingLoggerProvider : LoggerProvider
{
    private readonly ConcurrentQueue<ILoggerEntry> _entries = new();

    public override string Name => "Recording";

    public IReadOnlyList<ILoggerEntry> Entries => [.. _entries];

    protected override Logger CreateCore(string category) => new RecordingLogger(category, this);

    private void Record(ILoggerEntry entry) => _entries.Enqueue(entry);

    private sealed class RecordingLogger : Logger
    {
        private readonly RecordingLoggerProvider _provider;

        public RecordingLogger(string category, RecordingLoggerProvider provider)
            : base(category)
        {
            _provider = provider;
        }

        protected override void WriteCore(ILoggerEntry entry) => _provider.Record(entry);

        protected override IScopedLogger BeginScopeCore(ILoggerEntry entry)
        {
            _provider.Record(entry);
            return new RecordingScopedLogger(Category, _provider, entry.Id);
        }
    }

    private sealed class RecordingScopedLogger : ScopedLogger
    {
        private readonly RecordingLoggerProvider _provider;

        public RecordingScopedLogger(string category, RecordingLoggerProvider provider, LogId parentId)
            : base(category, parentId)
        {
            _provider = provider;
        }

        protected override void WriteCore(ILoggerEntry entry) => _provider.Record(entry);

        protected override IScopedLogger BeginScopeCore(ILoggerEntry entry)
        {
            _provider.Record(entry);
            return new RecordingScopedLogger(Category, _provider, entry.Id);
        }
    }
}
