using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Hosting.Health;

namespace Assimalign.Cohesion.Database.Hosting.Internal;

// Hosting observes health; the model owns its transitions. Serialize enabled observations
// only, and release the gate before calling listeners (which may reenter health).
internal sealed class DatabaseHealthTrace
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<DatabaseEngine, Dictionary<string, Condition>> _conditions = new(ReferenceEqualityComparer.Instance);

    internal async ValueTask<HealthContribution> CheckAsync(DatabaseApplicationContext context, CancellationToken cancellationToken)
    {
        HealthContribution result;
        var changes = new List<(DatabaseEngine Engine, string Database, Condition Condition, bool Restored)>();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            result = await context.CheckCoreAsync(cancellationToken).ConfigureAwait(false);
            var current = new Dictionary<DatabaseEngine, Dictionary<string, Condition>>(ReferenceEqualityComparer.Instance);
            IReadOnlyList<ReopenState> reopening = context.ReopenService?.GetPendingReopens() ?? [];
            foreach (var engine in context.Engines)
            {
                if (engine.State == EngineState.Disposed)
                {
                    continue;
                }
                var conditions = new Dictionary<string, Condition>(StringComparer.OrdinalIgnoreCase);
                current.Add(engine, conditions);
                _conditions.TryGetValue(engine, out var previous);
                var offline = new HashSet<DatabaseName>(engine.OfflineDatabases);
                await foreach (var database in engine.GetDatabasesAsync(cancellationToken).ConfigureAwait(false))
                {
                    string name = database.Name.ToString();
                    DatabaseProvisioningProgress? progress = database.Provisioning;
                    if (offline.Contains(database.Name))
                    {
                        conditions[name] = new Condition("Offline", DatabaseReopenService.GetCause(engine, name), progress);
                    }
                    else if (progress is not null)
                    {
                        conditions[name] = new Condition("Provisioning", null, progress);
                    }
                    else if (previous?.TryGetValue(name, out var restored) == true)
                    {
                        // A successful reopen may still be present in the service snapshot.
                        bool pending = false;
                        foreach (var entry in reopening)
                        {
                            if (ReferenceEquals(entry.Engine, engine) && string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                            {
                                pending = true;
                                break;
                            }
                        }
                        if (!pending)
                        {
                            changes.Add((engine, name, restored, true));
                        }
                    }
                }
                foreach (var entry in reopening)
                {
                    if (ReferenceEquals(entry.Engine, engine))
                    {
                        conditions[entry.Name] = new Condition("Offline", entry.Cause, conditions.GetValueOrDefault(entry.Name)?.Progress);
                    }
                }
                foreach (var entry in conditions)
                {
                    if (previous?.TryGetValue(entry.Key, out var prior) != true ||
                        prior is null || prior.Reason != entry.Value.Reason || prior.Cause != entry.Value.Cause)
                    {
                        changes.Add((engine, entry.Key, entry.Value, false));
                    }
                }
            }
            // Missing/dropped databases and disposed engines are forgotten, never called restored.
            _conditions = current;
        }
        finally
        {
            _gate.Release();
        }
        foreach (var change in changes)
        {
            if (change.Restored)
            {
                DatabaseHostingEventSource.Log.HealthRestored(context, change.Engine, change.Database,
                    change.Condition.Reason, change.Condition.Cause, change.Condition.Progress);
            }
            else
            {
                DatabaseHostingEventSource.Log.HealthDegraded(context, change.Engine, change.Database,
                    change.Condition.Reason, change.Condition.Cause, change.Condition.Progress);
            }
        }
        return result;
    }

    private sealed record Condition(string Reason, StorageOfflineCause? Cause, DatabaseProvisioningProgress? Progress);
}
