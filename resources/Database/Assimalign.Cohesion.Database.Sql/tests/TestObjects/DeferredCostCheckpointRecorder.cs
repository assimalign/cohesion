using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

/// <summary>Pairs synchronous checkpoint events with physical writes in that file set.</summary>
internal sealed class DeferredCostCheckpointRecorder : EventListener
{
    private readonly CrashCaptureSqlStorageStrategy _storage;
    private readonly EventSource _source;
    private readonly string _dataName;
    private readonly string _catalogName;
    private readonly Func<string> _phase;
    private readonly Action<string> _write;
    private readonly Dictionary<string, (long Before, string Trigger, string Phase, long Journal)> _starts = new();

    internal DeferredCostCheckpointRecorder(CrashCaptureSqlStorageStrategy storage, EventSource source,
        string dataName, string catalogName, Func<string> phase, Action<string> write)
    {
        _storage = storage;
        _source = source;
        _dataName = dataName;
        _catalogName = catalogName;
        _phase = phase;
        _write = write;
        EnableEvents(source, EventLevel.Informational, EventKeywords.All);
    }

    protected override void OnEventWritten(EventWrittenEventArgs entry)
    {
        if (!ReferenceEquals(entry.EventSource, _source) || entry.EventId is not (5 or 6) ||
            entry.Payload?[0] is not string name || name != _dataName && name != _catalogName) { return; }
        bool catalog = name == _catalogName;
        lock (_starts)
        {
            if (entry.EventId == 5)
            {
                // The event is synchronous: the stack identifies the caller without
                // adding a production hook or changing when a checkpoint is requested.
                string stack = Environment.StackTrace;
                string trigger = stack.Contains("RunDeferred", StringComparison.Ordinal) ? "gate-release" :
                    stack.Contains("SqlCheckpointWorker.Checkpoint", StringComparison.Ordinal) ? "worker" :
                    stack.Contains("CheckpointReplacementCopyAsync", StringComparison.Ordinal) ? "forced-copy" : "explicit";
                _starts[name] = (_storage.PageWrites("typed", catalog), trigger, _phase(), (long)entry.Payload[2]!);
            }
            else if (_starts.Remove(name, out var start))
            {
                _write($"file={(catalog ? "catalog" : "data")}|trigger={start.Trigger}|phase={start.Phase}|journal={start.Journal}|pages={_storage.PageWrites("typed", catalog) - start.Before}|status={entry.Payload![1]}");
            }
        }
    }
}
