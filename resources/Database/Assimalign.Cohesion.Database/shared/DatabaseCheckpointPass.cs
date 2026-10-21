using System;
using System.Collections.Generic;
using System.Threading;

// Deviates from namespace-matches-assembly: this model-local helper is compiled
// from the Database root's shared source into each model assembly.
namespace Assimalign.Cohesion.Database;

// An opt-in observation, owned by the pump until publication. It retains names, never database
// instances, and no history in the worker. A consumer chooses how many completed passes to keep.
internal sealed class DatabaseCheckpointPass
{
    private readonly List<Visit> _visits = [];

    internal DatabaseCheckpointPass(long number, TimeSpan? pumpBackoff, TimeSpan? pumpElapsed, TimeSpan triggerTimeout, TimeSpan triggerElapsed, bool triggerSignaled, bool triggerMeasured)
    {
        Number = number;
        PumpBackoff = pumpBackoff;
        PumpElapsed = pumpElapsed;
        TriggerTimeout = triggerTimeout;
        TriggerElapsed = triggerElapsed;
        TriggerSignaled = triggerSignaled;
        TriggerMeasured = triggerMeasured;
        Visits = _visits.AsReadOnly();
    }

    internal long Number { get; }
    // Null means the root pump's delay hook was not connected, rather than zero delays.
    internal TimeSpan? PumpBackoff { get; }
    internal TimeSpan? PumpElapsed { get; }
    internal TimeSpan TriggerTimeout { get; }
    internal TimeSpan TriggerElapsed { get; }
    internal bool TriggerSignaled { get; }
    internal bool TriggerMeasured { get; }
    internal bool Cancelled { get; set; }
    internal bool PassThrew { get; set; }
    internal IReadOnlyList<Visit> Visits { get; }

    internal Visit Add(string database)
    {
        var visit = new Visit(database);
        _visits.Add(visit);
        return visit;
    }

    internal sealed class Visit
    {
        private int _laneWaits;

        internal Visit(string database) => Database = database;

        internal string Database { get; }
        internal VisitReason Reason { get; set; } = VisitReason.Interrupted;
        internal bool Attempted { get; set; }
        internal bool Collected { get; set; }
        internal bool GiveUpRequested { get; set; }
        internal bool Offline { get; set; }
        internal DueState? Due { get; set; }
        internal TimeSpan Backoff { get; set; }
        internal TimeSpan LaneWait { get; set; }
        internal TimeSpan LaneWaitRequested { get; set; }
        internal int LaneWaits
        {
            get => Volatile.Read(ref _laneWaits);
            set => Volatile.Write(ref _laneWaits, value);
        }
    }

    internal enum VisitReason
    {
        Interrupted,
        Running,
        Offline,
        BackingOff,
        NotDue,
        Completed,
        Deferred,
        Busy,
        Closed,
        Cancelled,
        Failed,
    }

    // These are the model predicate's inputs, captured independently of its return value.
    // Storage due flags include the storage's size and time backstops; the lengths and
    // thresholds are diagnostic samples that can change concurrently with the predicate.
    internal readonly record struct DueState(bool Closed, bool DataDue, bool CatalogDue,
        long DataLength, long DataThreshold, long CatalogLength = 0, long CatalogThreshold = 0)
    {
        internal bool IsDue => !Closed && (DataDue || CatalogDue);
    }
}
