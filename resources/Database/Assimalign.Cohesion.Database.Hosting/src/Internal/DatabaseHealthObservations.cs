using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Web.Health;

namespace Assimalign.Cohesion.Database.Hosting.Internal;

// Receives only values already read by the common health core. No engine or database reads here.
[System.Diagnostics.DebuggerDisplay("DatabaseHealthObservations")]
[System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class DatabaseHealthObservations
{
    internal const string JournalAdmissionPauseDescription = "CK06006: Write admission is paused for a pending journal checkpoint{0}. Reads retain their ordinary snapshot and locking rules.";

    private readonly List<DatabaseHealthObservation> _engines = [];
    private readonly Dictionary<string, DatabaseHealthObservation> _databases = new(StringComparer.Ordinal);
    private readonly List<string> _databaseNames = [];
    private readonly string? _componentName;
    private readonly bool _databaseComponent;
    private HealthCheckResult? _failure;

    internal DatabaseHealthObservations(string? componentName = null)
    {
        _componentName = componentName;
        _databaseComponent = componentName?.StartsWith("database/", StringComparison.Ordinal) == true;
    }

    internal bool WantsDatabase(string engine, string name)
        => _componentName is null || _databaseComponent && string.Equals(_componentName, DatabaseName(engine, name), StringComparison.Ordinal);

    internal void Engine(string name, EngineState state, Dictionary<string, object> data, string prefix)
    {
        string componentName = EngineName(name);
        if (_componentName is not null && _componentName != componentName) { return; }
        var detail = new Dictionary<string, object>(StringComparer.Ordinal);
        string provisioningPrefix = prefix + "provisioning.";
        string offlinePrefix = prefix + "offline.";
        foreach (var entry in data)
        {
            if (entry.Key.StartsWith(prefix, StringComparison.Ordinal) &&
                !entry.Key.StartsWith(provisioningPrefix, StringComparison.Ordinal) &&
                !entry.Key.StartsWith(offlinePrefix, StringComparison.Ordinal))
            {
                detail.Add(entry.Key[prefix.Length..], entry.Value);
            }
        }
        HealthCheckResult result = EngineStatus(state) switch
        {
            HealthStatus.Healthy => HealthCheckResult.Healthy($"Database engine '{name}' is running.", detail),
            HealthStatus.Degraded => HealthCheckResult.Degraded($"Database engine '{name}' has worker faults.", data: detail),
            _ => HealthCheckResult.Unhealthy($"Database engine '{name}' is unavailable.", data: detail),
        };
        _engines.Add(new DatabaseHealthObservation(componentName, result));
    }

    internal void Database(string engine, string name, bool paused, string? provisioning, Dictionary<string, object>? data)
    {
        string componentName = DatabaseName(engine, name);
        if (!paused && provisioning is null)
        {
            SetDatabase(componentName, new DatabaseHealthObservation(componentName, engine, name));
            return;
        }
        data ??= new Dictionary<string, object>(StringComparer.Ordinal);
        data["engine"] = engine;
        data["database"] = name;
        if (paused) { data["journalAdmissionPauseCode"] = "CK06006"; }
        var result = paused
            ? HealthCheckResult.Degraded(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                JournalAdmissionPauseDescription, string.Empty), data: data)
            : provisioning is not null
                ? HealthCheckResult.Degraded($"Provisioning database: {provisioning}.", data: data)
                : HealthCheckResult.Healthy("The database is online.", data);
        SetDatabase(componentName, new DatabaseHealthObservation(componentName, result));
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
        SetDatabase(key, new DatabaseHealthObservation(key, HealthCheckResult.Unhealthy($"Offline database: {description}.", data: detail)));
    }

    private void SetDatabase(string name, DatabaseHealthObservation result)
    {
        if (!_databases.ContainsKey(name)) { _databaseNames.Add(name); }
        _databases[name] = result;
    }

    internal void Failure(string engine, string fault)
    {
        var result = HealthCheckResult.Unhealthy($"Database engine '{engine}' health evaluation failed: {fault}.");
        _failure = result;
        string name = EngineName(engine);
        int index = _engines.FindIndex(value => value.Name == name);
        var observation = new DatabaseHealthObservation(name, result);
        if (index >= 0) { _engines[index] = observation; } else { _engines.Add(observation); }
        string prefix = DatabasePrefix(engine);
        foreach (var database in _databaseNames)
        {
            if (database.StartsWith(prefix, StringComparison.Ordinal)) { _databases[database] = new DatabaseHealthObservation(database, result); }
        }
    }

    internal DatabaseHealthObservation GetComponent(string name)
    {
        if (_failure is { } failure) { return new DatabaseHealthObservation(name, failure); }
        foreach (var engine in _engines) { if (engine.Name == name) { return engine; } }
        return _databases.TryGetValue(name, out var result)
            ? result : new DatabaseHealthObservation(name, HealthCheckResult.Unhealthy("The database component is not open."));
    }

    internal IReadOnlyList<DatabaseHealthObservation> Complete(HealthCheckResult readiness)
    {
        var result = new List<DatabaseHealthObservation>();
        foreach (var engine in _engines)
        {
            result.Add(engine);
            string prefix = "database/" + engine.Name["engine/".Length..] + "/";
            var names = _databaseNames.FindAll(name => name.StartsWith(prefix, StringComparison.Ordinal));
            names.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string name in names) { result.Add(_databases[name]); }
        }
        result.Add(new DatabaseHealthObservation("database.accepting", readiness));
        return result.AsReadOnly();
    }

    internal static string EngineName(string engine) => $"engine/{Uri.EscapeDataString(engine)}";

    internal static HealthStatus EngineStatus(EngineState state) => state switch
    {
        EngineState.Running => HealthStatus.Healthy,
        EngineState.Faulted => HealthStatus.Degraded,
        _ => HealthStatus.Unhealthy,
    };

    internal static string DatabasePrefix(string engine) => $"database/{Uri.EscapeDataString(engine)}/";

    // Match the storage fold for ASCII letters without folding distinct custom names such as K and the Kelvin sign.
    internal static string DatabaseName(string engine, string database)
    {
        string folded = string.Create(database.Length, database, static (characters, name) =>
        {
            for (int index = 0; index < name.Length; index++)
            {
                char character = name[index];
                characters[index] = char.IsAsciiLetterUpper(character) ? (char)(character + ('a' - 'A')) : character;
            }
        });
        return DatabasePrefix(engine) + Uri.EscapeDataString(folded);
    }

    private sealed class DebugView
    {
        public DebugView(DatabaseHealthObservations value) { _ = value; }
    }
}
