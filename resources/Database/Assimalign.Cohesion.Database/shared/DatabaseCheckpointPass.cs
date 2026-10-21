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

    internal DatabaseCheckpointPass(long number, TimeSpan pumpBackoff, TimeSpan triggerTimeout, TimeSpan triggerElapsed, bool triggerSignaled)
    {
        Number = number;
        PumpBackoff = pumpBackoff;
        TriggerTimeout = triggerTimeout;
        TriggerElapsed = triggerElapsed;
        TriggerSignaled = triggerSignaled;
        Visits = _visits.AsReadOnly();
    }

    internal long Number { get; }
    internal TimeSpan PumpBackoff { get; }
    internal TimeSpan TriggerTimeout { get; }
    internal TimeSpan TriggerElapsed { get; }
    internal bool TriggerSignaled { get; }
    internal bool Cancelled { get; set; }
    internal bool Failed { get; set; }
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
        internal string Reason { get; set; } = "Cancelled";
        internal TimeSpan Backoff { get; set; }
        internal TimeSpan LaneWait { get; set; }
        internal TimeSpan LaneWaitRequested { get; set; }
        internal int LaneWaits
        {
            get => Volatile.Read(ref _laneWaits);
            set => Volatile.Write(ref _laneWaits, value);
        }
    }
}
