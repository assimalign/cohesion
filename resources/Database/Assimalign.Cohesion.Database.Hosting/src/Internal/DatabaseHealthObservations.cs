using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Web.Health;

namespace Assimalign.Cohesion.Database.Hosting.Internal;

// Receives only values already read by the common health core. No engine or database reads here.
[System.Diagnostics.DebuggerDisplay("DatabaseHealthObservations")]
[System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class DatabaseHealthObservations
{
    private readonly List<DatabaseHealthObservation> _engines = [];
    private readonly Dictionary<string, HealthCheckResult> _databases = new(StringComparer.Ordinal);

    internal void Engine(string name, EngineState state, Dictionary<string, object> data, string prefix)
    {
        var detail = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var entry in data)
        {
            if (entry.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                detail.Add(entry.Key[prefix.Length..], entry.Value);
            }
        }
        HealthCheckResult result = state switch
        {
            EngineState.Running => HealthCheckResult.Healthy($"Database engine '{name}' is running.", detail),
            EngineState.Faulted => HealthCheckResult.Degraded($"Database engine '{name}' has worker faults.", data: detail),
            _ => HealthCheckResult.Unhealthy($"Database engine '{name}' is unavailable.", data: detail),
        };
        _engines.Add(new DatabaseHealthObservation($"engine/{Uri.EscapeDataString(name)}", result));
    }

    internal void Database(string engine, string name, bool paused, string? provisioning, Dictionary<string, object> data)
    {
        data["engine"] = engine;
        data["database"] = name;
        var result = paused
            ? HealthCheckResult.Degraded("CK06006: Write admission is paused for a pending journal checkpoint. Reads retain their ordinary snapshot and locking rules.", data: data)
            : provisioning is not null
                ? HealthCheckResult.Degraded($"Provisioning database: {provisioning}.", data: data)
                : HealthCheckResult.Healthy("The database is online.", data);
        if (paused) { data["journalAdmissionPauseCode"] = "CK06006"; }
        _databases[DatabaseName(engine, name)] = result;
    }

    internal void Offline(string engine, string name, string description, Dictionary<string, object> data)
    {
        string key = DatabaseName(engine, name);
        var detail = new Dictionary<string, object>(StringComparer.Ordinal);
        if (_databases.TryGetValue(key, out var prior) && prior.Data is not null)
        {
            foreach (var entry in prior.Data) { detail.Add(entry.Key, entry.Value); }
        }
        foreach (var entry in data) { detail[entry.Key] = entry.Value; }
        detail["engine"] = engine;
        detail["database"] = name;
        _databases[key] = HealthCheckResult.Unhealthy($"Offline database: {description}.", data: detail);
    }

    internal IReadOnlyList<DatabaseHealthObservation> Complete(HealthCheckResult readiness)
    {
        var result = new List<DatabaseHealthObservation>(_engines);
        foreach (var database in _databases)
        {
            result.Add(new DatabaseHealthObservation(database.Key, database.Value));
        }
        result.Add(new DatabaseHealthObservation("database.accepting", readiness));
        return result.AsReadOnly();
    }

    private static string DatabaseName(string engine, string database)
        => $"database/{Uri.EscapeDataString(engine)}/{Uri.EscapeDataString(database.ToUpperInvariant())}";

    private sealed class DebugView
    {
        public DebugView(DatabaseHealthObservations value) { _ = value; }
    }
}
