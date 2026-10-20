using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Runtime.ExceptionServices;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Blob.Tests;

/// <summary>
/// Bounded aggregation of the kernel's existing checkpoint events for #1283. Unlike a general
/// event recorder this retains only checkpoint-cycle totals, even during a sustained write load.
/// No shipped diagnostics or friend-assembly access to the kernel is needed.
/// </summary>
internal sealed class CheckpointProbe : EventListener
{
    private const string storageSource = "Assimalign.Cohesion.Database.Storage";
    private const string transactionSource = "Assimalign.Cohesion.Database.Transactions";
    private const string workerSource = "Assimalign.Cohesion.Database";
    private readonly string _database;
    private readonly string _worker;
    private readonly object _sync = new();
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly List<long> _checkpointsBySecond = [];
    private long _checkpoints;
    private long _deferred;
    private long _bracketOpen;
    private long _offline;
    private long _failures;
    private long _eventErrors;
    private long _peak;
    private long _triggers;
    private long _triggeredAt;
    private long _deferredAt;
    private long _writesAtTrigger;
    private long _writesAtDeferral;
    private long _writes;
    private long _maxWritesBeforeCheckpoint;
    private double _maxTriggerDelay;
    private double _maxDeferredDelay;
    private Cycle? _largestCycle;
    private long _passes;
    private long _unfinished;
    private long _passesAtTrigger;
    private long _unfinishedAtTrigger;
    private bool _inPass;
    private bool _inPassAtTrigger;
    private readonly bool _countStorageTransactionExceptions;
    private long _storageTransactionExceptions;

    internal CheckpointProbe(string database, string engine, bool countStorageTransactionExceptions = false)
    {
        _database = database;
        _worker = engine + "/checkpoint";
        _countStorageTransactionExceptions = countStorageTransactionExceptions;
        if (_countStorageTransactionExceptions)
        {
            // Test-only, process-wide first-chance count: it includes caught busy-storage
            // refusals, which happen before CheckpointStart and have no storage event.
            AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
        }
        // Explicitly select the existing providers by name after the engine initialized them.
        // Listen only to checkpoint keywords, not every row/version/lock event in the workload.
        foreach (var source in EventSource.GetSources().Where(source => source.Name is storageSource or transactionSource or workerSource))
        {
            EnableEvents(source, EventLevel.Verbose, (EventKeywords)(source.Name == storageSource ? 1 : source.Name == transactionSource ? 4 : 1));
        }
    }

    internal void Triggered(long length)
    {
        lock (_sync)
        {
            _triggers++;
            _triggeredAt = Stopwatch.GetTimestamp();
            _deferredAt = 0;
            _writesAtTrigger = _writes;
            _passesAtTrigger = _passes;
            _unfinishedAtTrigger = _unfinished;
            _inPassAtTrigger = _inPass;
            _peak = Math.Max(_peak, length);
        }
    }

    internal void Written(long length)
    {
        lock (_sync)
        {
            _writes++;
            _peak = Math.Max(_peak, length);
        }
    }

    internal Snapshot Read()
    {
        lock (_sync)
        {
            return new(_watch.Elapsed, _writes, _checkpoints, _deferred, _bracketOpen, _offline,
                _failures, _eventErrors, _peak, _triggers, _maxWritesBeforeCheckpoint,
                _maxTriggerDelay, _maxDeferredDelay, _checkpointsBySecond.ToArray(), _largestCycle, _passes, _unfinished,
                _countStorageTransactionExceptions ? _storageTransactionExceptions : null);
        }
    }

    public override void Dispose()
    {
        if (_countStorageTransactionExceptions)
        {
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;
        }
        base.Dispose();
    }

    private void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs data)
    {
        if (data.Exception is StorageTransactionException)
        {
            lock (_sync)
            {
                _storageTransactionExceptions++;
            }
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs data)
    {
        // Lock order: storage/coordinator locks may call this listener, then take _sync.
        // Nothing holding _sync calls storage, coordinator, or EventSource APIs. Callers read
        // JournalLength before Written/Triggered; reversing that order could deadlock a checkpoint.
        lock (_sync)
        {
            if (data.EventId == 0)
            {
                _eventErrors++;
                return;
            }
            if (data.Payload is not { Count: > 0 } payload)
            {
                return;
            }
            if (data.EventSource.Name == workerSource)
            {
                if (Equals(payload[0], _worker))
                {
                    switch (data.EventId)
                    {
                        case 14: _passes++; _inPass = true; break;
                        case 15: _inPass = false; break;
                        case 16: _unfinished++; break;
                    }
                }
                return;
            }
            if (!Equals(payload[0], _database))
            {
                return;
            }
            if (data.EventSource.Name == storageSource)
            {
                if (data.EventId == 5)
                {
                    long length = (long)payload[2]!;
                    _peak = Math.Max(_peak, length);
                    if (_triggeredAt != 0 && (_largestCycle is null || length > _largestCycle.Length))
                    {
                        _largestCycle = new(length, _writes - _writesAtTrigger,
                            _deferredAt == 0 ? null : _writesAtDeferral - _writesAtTrigger,
                            Stopwatch.GetElapsedTime(_triggeredAt).TotalMilliseconds,
                            _deferredAt == 0 ? null : Stopwatch.GetElapsedTime(_deferredAt).TotalMilliseconds,
                            _passes - _passesAtTrigger, _unfinished - _unfinishedAtTrigger, _inPassAtTrigger);
                    }
                    if (_triggeredAt != 0)
                    {
                        _maxTriggerDelay = Math.Max(_maxTriggerDelay, Stopwatch.GetElapsedTime(_triggeredAt).TotalMilliseconds);
                        _maxWritesBeforeCheckpoint = Math.Max(_maxWritesBeforeCheckpoint, _writes - _writesAtTrigger);
                        _triggeredAt = 0;
                    }
                    if (_deferredAt != 0)
                    {
                        _maxDeferredDelay = Math.Max(_maxDeferredDelay, Stopwatch.GetElapsedTime(_deferredAt).TotalMilliseconds);
                        _deferredAt = 0;
                    }
                }
                else if (data.EventId == 6)
                {
                    if (Equals(payload[1], "Success"))
                    {
                        _checkpoints++;
                        int second = (int)_watch.Elapsed.TotalSeconds;
                        while (_checkpointsBySecond.Count <= second)
                        {
                            _checkpointsBySecond.Add(0);
                        }
                        _checkpointsBySecond[second]++;
                    }
                    else
                    {
                        _failures++;
                    }
                }
            }
            else if (data.EventSource.Name == transactionSource)
            {
                switch (data.EventId)
                {
                    case 15:
                        _deferred++;
                        if (_deferredAt == 0)
                        {
                            _deferredAt = Stopwatch.GetTimestamp();
                            _writesAtDeferral = _writes;
                        }
                        break;
                    case 16 when Equals(payload[1], "BracketOpen"):
                        _bracketOpen++;
                        break;
                    case 16:
                        _offline++;
                        break;
                    case 17:
                        _failures++;
                        break;
                }
            }
        }
    }

    internal sealed record Snapshot(TimeSpan Elapsed, long Writes, long Checkpoints, long Deferred,
        long BracketOpen, long Offline, long Failures, long EventErrors, long Peak, long Triggers,
        long MaxWritesBeforeCheckpoint, double MaxTriggerDelay, double MaxDeferredDelay, long[] CheckpointsBySecond,
        Cycle? LargestCycle, long WorkerPasses, long WorkerUnfinished, long? StorageTransactionExceptions)
    {
        internal string Describe(long size)
            => $"{Writes} commits; {Checkpoints} checkpoints in {Elapsed.TotalSeconds:F2}s " +
                $"({Checkpoints / Elapsed.TotalSeconds:F2}/s; buckets [{string.Join(",", CheckpointsBySecond)}]); " +
                $"peak >= {Peak} bytes ({(double)Peak / size:F2} triggers); triggers={Triggers}, deferred={Deferred}, " +
                $"BracketOpen={BracketOpen}, StorageTransactionException(process)={StorageTransactionExceptions?.ToString() ?? "not counted"}, " +
                $"offline={Offline}, failures={Failures}, eventErrors={EventErrors}; " +
                $"max trigger-to-start={MaxTriggerDelay:F2}ms/{MaxWritesBeforeCheckpoint} commits, " +
                $"max defer-to-start={MaxDeferredDelay:F2}ms; worker passes={WorkerPasses}, unfinished={WorkerUnfinished}; largest cycle={LargestCycle}";
    }

    internal sealed record Cycle(long Length, long WritesAfterTrigger, long? WritesBeforeDeferral,
        double TriggerToStartMilliseconds, double? DeferToStartMilliseconds, long WorkerPassesAfterTrigger,
        long WorkerUnfinishedAfterTrigger, bool WorkerInPassAtTrigger);
}
