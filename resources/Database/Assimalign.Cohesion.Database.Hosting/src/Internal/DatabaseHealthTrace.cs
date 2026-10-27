using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Hosting.Health;

namespace Assimalign.Cohesion.Database.Hosting.Internal;

// The core supplies its own observations. Tracing never reads an engine again or
// adds a cancellation point; only completed checks enter the transition gate.
[global::System.Diagnostics.DebuggerDisplay("DatabaseHealthTrace")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Hosting.Internal.DatabaseHealthTrace.DebugView))]
internal sealed class DatabaseHealthTrace
{
    private readonly Lock _gate = new();
    private readonly Queue<List<Change>> _pending = new();
    private Dictionary<DatabaseEngine, Dictionary<string, Condition>> _conditions = new(ReferenceEqualityComparer.Instance);
    private bool _emitting;
    private long _nextObservation;
    private long _lastObservation;

    internal async ValueTask<HealthContribution> CheckAsync(DatabaseApplicationContext context, CancellationToken cancellationToken)
    {
        long sequence = Interlocked.Increment(ref _nextObservation);
        var observation = new Observation();
        var result = await context.CheckCoreAsync(cancellationToken, observation).ConfigureAwait(false);
        lock (_gate)
        {
            // A slower, earlier check still returns its own result, but cannot replace
            // a successful observation from a check that began after it.
            if (sequence < _lastObservation) { return result; }
            _lastObservation = sequence;
            var changes = new List<Change>();
            foreach (var engine in observation.Conditions)
            {
                _conditions.TryGetValue(engine.Key, out var previous);
                foreach (var entry in engine.Value)
                {
                    Condition? prior = previous?.GetValueOrDefault(entry.Key);
                    var condition = entry.Value;
                    if (condition.Instance is null && prior?.Instance is not null)
                    {
                        // Recovery can temporarily remove the instance from inventory.
                        // Retain its identity until an actual replacement is observed.
                        condition = condition with { Instance = prior.Instance };
                        engine.Value[entry.Key] = condition;
                    }
                    if (condition.Reason is null)
                    {
                        if (prior?.Reason is not null)
                        {
                            changes.Add(new Change(engine.Key, entry.Key, prior, Restored: true));
                        }
                    }
                    else if (prior?.Reason != condition.Reason || prior.Cause != condition.Cause ||
                        (condition.Instance is not null && !ReferenceEquals(prior.Instance, condition.Instance)))
                    {
                        changes.Add(new Change(engine.Key, entry.Key, condition, Restored: false));
                    }
                }
            }
            // Missing databases and disposed engines are forgotten, never called restored.
            _conditions = observation.Conditions;
            if (changes.Count > 0) { _pending.Enqueue(changes); }
            if (_emitting || _pending.Count == 0) { return result; }
            _emitting = true;
        }

        // One emitter drains whole batches in observation order. Reentrant and concurrent
        // checks enqueue and return, so no listener runs under the observation gate.
        while (true)
        {
            List<Change> changes;
            lock (_gate)
            {
                if (!_pending.TryDequeue(out changes!))
                {
                    _emitting = false;
                    return result;
                }
            }
            foreach (var change in changes)
            {
                if (change.Restored)
                {
                    DatabaseHostingEventSource.Log.HealthRestored(context, change.Engine, change.Database,
                        change.Condition.Reason!, change.Condition.Cause, change.Condition.Progress);
                }
                else
                {
                    DatabaseHostingEventSource.Log.HealthDegraded(context, change.Engine, change.Database,
                        change.Condition.Reason!, change.Condition.Cause, change.Condition.Progress);
                }
            }
        }
    }

    [global::System.Diagnostics.DebuggerDisplay("Observation")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Hosting.Internal.DatabaseHealthTrace.Observation.DebugView))]
    internal sealed class Observation
    {
        internal Dictionary<DatabaseEngine, Dictionary<string, Condition>> Conditions { get; } = new(ReferenceEqualityComparer.Instance);

        internal void ObserveDatabase(DatabaseEngine engine, DatabaseInstance database, DatabaseProvisioningProgress? progress)
            => ForEngine(engine)[database.Name.ToString()] = new Condition(database, progress is null ? null : "Provisioning", null, progress);

        internal void ObserveOffline(DatabaseEngine engine, string name, StorageOfflineCause? cause)
        {
            var conditions = ForEngine(engine);
            var observed = conditions.GetValueOrDefault(name);
            conditions[name] = new Condition(observed?.Instance, "Offline", cause, observed?.Progress);
        }

        private Dictionary<string, Condition> ForEngine(DatabaseEngine engine)
        {
            if (!Conditions.TryGetValue(engine, out var conditions))
            {
                conditions = new Dictionary<string, Condition>(StringComparer.OrdinalIgnoreCase);
                Conditions.Add(engine, conditions);
            }
            return conditions;
        }

        private sealed class DebugView
        {
            public DebugView(global::Assimalign.Cohesion.Database.Hosting.Internal.DatabaseHealthTrace.Observation value)
            {
                _ = value;
            }
        }
    }

    internal sealed record Condition(DatabaseInstance? Instance, string? Reason, StorageOfflineCause? Cause, DatabaseProvisioningProgress? Progress);

    private sealed record Change(DatabaseEngine Engine, string Database, Condition Condition, bool Restored);

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Hosting.Internal.DatabaseHealthTrace value)
        {
            _ = value;
        }
    }
}
