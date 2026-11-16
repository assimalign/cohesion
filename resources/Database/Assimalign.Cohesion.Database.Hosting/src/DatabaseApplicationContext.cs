using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.Database.Hosting.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Web.Health;

namespace Assimalign.Cohesion.Database.Hosting;

using Assimalign.Cohesion.Hosting;

/// <summary>
/// The host context for <see cref="DatabaseApplication"/> — the concrete
/// <see cref="IDatabaseApplicationContext"/>: final infrastructure, every engine,
/// and the servers flattened from those engines.
/// </summary>
/// <remarks>
/// Engine factories observe preceding construction through read-only snapshots. The complete
/// engine/server registries are frozen before service factories run; retained options cannot
/// change the running context.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("DatabaseApplicationContext")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Hosting.DatabaseApplicationContext.DebugView))]
public sealed class DatabaseApplicationContext : HostContext, IDatabaseApplicationContext
{
    /// <summary>
    /// The name of the database application's entry in a health report.
    /// </summary>
    internal const string HealthCheckName = "database";

    private DatabaseHealthTrace? _healthTrace;
    private readonly HostEnvironment _environment;
    private IReadOnlyList<DatabaseEngine> _engines;
    private IReadOnlyList<DatabaseServer> _servers;
    private IReadOnlyList<IHostService> _hostedServices = [];

    internal DatabaseApplicationContext(
        HostEnvironment environment,
        ConfigurationManager configuration,
        ServiceProvider services,
        LoggerFactory loggerFactory,
        CancellationToken buildCancellationToken = default)
    {
        Configuration = configuration;
        Services = services;
        _environment = environment;
        BuildContext = new DatabaseApplicationBuildContext(environment, configuration, services, loggerFactory, buildCancellationToken);
        BuildCancellationToken = buildCancellationToken;
        _engines = [];
        _servers = [];
    }

    /// <summary>
    /// Gets the built host-level pieces handed to every owned engine factory of
    /// <see cref="DatabaseApplicationBuilder.AddEngine(string, Func{DatabaseApplicationBuildContext, DatabaseEngine})"/>.
    /// </summary>
    internal DatabaseApplicationBuildContext BuildContext { get; }

    /// <summary>
    /// Gets the host environment information.
    /// </summary>
    public override IHostEnvironment Environment => _environment;

    /// <summary>
    /// Gets the hosted services composed by the application.
    /// </summary>
    public override IEnumerable<IHostService> HostedServices => _hostedServices;

    /// <inheritdoc />
    public IReadOnlyList<DatabaseEngine> Engines => _engines;

    /// <inheritdoc />
    public CancellationToken BuildCancellationToken { get; }

    /// <inheritdoc />
    public IReadOnlyList<DatabaseServer> Servers => _servers;

    /// <summary>Gets loaded configuration; mutations do not recompose engine options.</summary>
    public IConfiguration Configuration { get; }

    /// <summary>Gets the application provider, borrowed until application disposal.</summary>
    public IServiceProvider Services { get; }

    /// <inheritdoc />
    public DatabaseEngine GetEngine(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        foreach (DatabaseEngine engine in _engines)
        {
            if (string.Equals(engine.Name, name, StringComparison.Ordinal))
            {
                return engine;
            }
        }
        throw new KeyNotFoundException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ContextGetEngineKeyNotFound, name));
    }

    /// <summary>
    /// Gets the application's reopen service (owner decision 22), or null when
    /// <see cref="DatabaseApplicationOptions.ReopenOfflineDatabases"/> is off. Set once at Build.
    /// </summary>
    internal DatabaseReopenService? ReopenService { get; set; }

    /// <summary>Gets live health membership without evaluating health or waiting for database lifecycle work.</summary>
    /// <remarks>
    /// Engines follow registration order; each engine is followed by its open, pending-reopen or recorded
    /// databases in ordinal ignore-case component-name order, then database.accepting follows all engines.
    /// A faulty inventory contributes its engine name. Re-read this member to follow opens and drops.
    /// </remarks>
    /// <exception cref="OutOfMemoryException">An engine inventory reports an out-of-memory failure.</exception>
    public IReadOnlyList<string> HealthComponentNames
    {
        get
        {
            var result = new List<string>();
            foreach (var engine in _engines)
            {
                result.Add(DatabaseHealthObservations.EngineName(engine.Name));
                IReadOnlyList<DatabaseInstance> databases;
                // Discovery must survive a custom engine's broken inventory; evaluation fails closed.
                try { databases = engine.OpenDatabases; }
                catch (Exception failure) when (failure is not OutOfMemoryException) { databases = []; }
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var database in databases) { names.Add(database.Name.ToString()); }
                foreach (var failure in engine.FailedOpens) { names.Add(failure.Name.ToString()); }
                foreach (var pending in ReopenService?.GetPendingReopens() ?? [])
                {
                    if (ReferenceEquals(pending.Engine, engine)) { names.Add(pending.Name); }
                }
                var ordered = names.Select(name => DatabaseHealthObservations.DatabaseName(engine.Name, name)).ToList();
                ordered.Sort(StringComparer.OrdinalIgnoreCase);
                result.AddRange(ordered);
            }
            result.Add("database.accepting");
            return result.AsReadOnly();
        }
    }

    /// <summary>
    /// Observes each engine, each currently open, reopening or failed-open database, and the host accept gate through
    /// the same evaluation core used by HTTP health. Reading health never opens, reopens or waits for a database.
    /// </summary>
    /// <param name="cancellationToken">Signals that the evaluation should be abandoned.</param>
    /// <returns>Immutable component observations, with invariant string data and bounded provisioning detail.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled.</exception>
    /// <exception cref="OutOfMemoryException">An engine evaluation reports an out-of-memory failure.</exception>
    /// <remarks>
    /// Evaluation failures other than caller cancellation and OutOfMemoryException become Unhealthy
    /// for that engine's components, with exception types only. Other engines and the accepting gate remain independent.
    /// Stored databases never attempted by this process are absent. Failed stored opens remain
    /// Unhealthy through DatabaseEngine.FailedOpens, including manual reopens with automatic reopen
    /// disabled. Open instances outrank pending reopens, which outrank failed-open records.
    /// </remarks>
    public async ValueTask<IReadOnlyList<DatabaseHealthObservation>> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var observations = new DatabaseHealthObservations();
        await CheckObservedAsync(cancellationToken, observations).ConfigureAwait(false);
        return observations.Complete(CheckReadiness());
    }

    /// <summary>Evaluates one named component, reading only its engine, or only the host accepting gate.</summary>
    /// <param name="componentName">A name from <see cref="HealthComponentNames"/>.</param>
    /// <param name="cancellationToken">Signals that evaluation should be abandoned.</param>
    /// <returns>An immutable observation; a database no longer open, pending or recorded is Unhealthy, "not open".</returns>
    /// <exception cref="ArgumentException"><paramref name="componentName"/> is null, empty or white space.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled.</exception>
    /// <exception cref="OutOfMemoryException">The engine evaluation reports an out-of-memory failure.</exception>
    /// <remarks>
    /// Evaluation failures other than caller cancellation and OutOfMemoryException become Unhealthy
    /// with exception types only. Cancellation of the caller's token propagates. The accepting gate reads no engine inventory. Re-read
    /// <see cref="HealthComponentNames"/> to follow membership changes.
    /// </remarks>
    public async ValueTask<DatabaseHealthObservation> GetHealthAsync(string componentName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentName);
        cancellationToken.ThrowIfCancellationRequested();
        if (componentName == "database.accepting") { return new DatabaseHealthObservation(componentName, CheckReadiness()); }
        foreach (var engine in _engines)
        {
            if (componentName != DatabaseHealthObservations.EngineName(engine.Name) &&
                !componentName.StartsWith(DatabaseHealthObservations.DatabasePrefix(engine.Name), StringComparison.Ordinal)) { continue; }
            var observations = new DatabaseHealthObservations(componentName);
            // A partial observation must not replace the aggregate trace's view of other engines.
            await CheckCoreAsync(cancellationToken, components: observations, onlyEngine: engine).ConfigureAwait(false);
            return observations.GetComponent(componentName);
        }
        return new DatabaseHealthObservation(componentName, HealthCheckResult.Unhealthy("The database component is not open."));
    }

    // Both the HTTP readiness check and transport-neutral observations use this outer-host gate.
    internal HealthCheckResult CheckReadiness()
    {
        var state = State;
        return state == HostState.Started
            ? HealthCheckResult.Healthy("All database servers are accepting connections.")
            : HealthCheckResult.Unhealthy($"The database application is '{state}' and is not accepting connections.");
    }

    /// <summary>
    /// Aggregates the state and worker inventory of every distinct registered or server-fronted
    /// database engine.
    /// </summary>
    /// <param name="cancellationToken">Signals that the health evaluation should be abandoned.</param>
    /// <returns>
    /// A healthy result when every engine is running, a degraded result while an engine reports a
    /// worker that keeps failing (the result names each failing worker and the type of its last
    /// failure, #1268), or an unhealthy result when an engine is disposed or reports an unknown
    /// state, or while a database is offline: its engine lists it offline (a failed write or
    /// durable flush of its journal, #1252 and #1243, durable flush of its data file, #1243, or
    /// file header write, #1268, or a background worker whose failures persisted or a journal past
    /// the engine's cap, owner decision 25), or the application's reopen of it has not succeeded
    /// yet (owner decision 22). The result names each offline database with its cause and its
    /// reopen attempts, and turns healthy once every one is open again.
    /// </returns>
    /// <remarks>
    /// The health endpoint is served without authentication, so a failure is described by its
    /// exception types only: an exception's message can carry file paths and storage internals.
    /// The full failure is written to the <c>Assimalign.Cohesion.Database</c> event source, and
    /// the reopen attempts to the <c>Assimalign.Cohesion.Database.Hosting</c> event source, which
    /// the application's logging forwards (<c>docs/EVENT_SOURCES.md</c>).
    /// </remarks>
    internal ValueTask<HealthCheckResult> CheckAsync(CancellationToken cancellationToken = default)
        => CheckObservedAsync(cancellationToken, null);

    private ValueTask<HealthCheckResult> CheckObservedAsync(CancellationToken cancellationToken, DatabaseHealthObservations? components)
    {
        if (!DatabaseHostingEventSource.Log.IsEnabled(System.Diagnostics.Tracing.EventLevel.Warning, System.Diagnostics.Tracing.EventKeywords.None) &&
            !DatabaseHostingEventSource.Log.IsEnabled(System.Diagnostics.Tracing.EventLevel.Informational, System.Diagnostics.Tracing.EventKeywords.None))
        {
            return CheckCoreAsync(cancellationToken, components: components);
        }
        var trace = LazyInitializer.EnsureInitialized(ref _healthTrace, static () => new DatabaseHealthTrace());
        return trace.CheckAsync(this, cancellationToken, components);
    }

    internal async ValueTask<HealthCheckResult> CheckCoreAsync(CancellationToken cancellationToken, DatabaseHealthTrace.Observation? observation = null,
        DatabaseHealthObservations? components = null, DatabaseEngine? onlyEngine = null)
        // Keep the existing async exception/cancellation surface even though inventory is synchronous.
        => await new ValueTask<HealthCheckResult>(CheckCore(cancellationToken, observation, components, onlyEngine)).ConfigureAwait(false);

    private HealthCheckResult CheckCore(CancellationToken cancellationToken, DatabaseHealthTrace.Observation? observation,
        DatabaseHealthObservations? components, DatabaseEngine? onlyEngine)
    {
        var engines = new List<DatabaseEngine>(_engines.Count + _servers.Count);
        var observedEngines = new HashSet<DatabaseEngine>(ReferenceEqualityComparer.Instance);

        foreach (DatabaseEngine engine in _engines)
        {
            if (observedEngines.Add(engine))
            {
                engines.Add(engine);
            }
        }

        foreach (DatabaseServer server in _servers)
        {
            DatabaseEngine engine = server.Engine;
            if (observedEngines.Add(engine))
            {
                engines.Add(engine);
            }
        }

        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["engineCount"] = engines.Count,
        };
        var faultedEngines = new List<string>();
        var failingWorkers = new List<string>();
        var unavailableEngines = new List<string>();
        var offlineDatabases = new List<string>();
        var provisioningDatabases = new List<string>();
        var pausedDatabases = new List<string>();
        int workerCount = 0;

        // The databases the application is reopening: a failed reopen left them closed, so their
        // engine no longer lists them, but they are not open again (owner decision 22).
        for (int engineIndex = 0; engineIndex < engines.Count; engineIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DatabaseEngine engine = engines[engineIndex];
            if (onlyEngine is not null && !ReferenceEquals(engine, onlyEngine)) { continue; }
            try
            {
                EngineState state = engine.State;
                IReadOnlyList<DatabaseEngineWorker> workers = engine.Workers;

                data[$"engine.{engineIndex}.name"] = engine.Name;
                data[$"engine.{engineIndex}.model"] = engine.Model.ToString();
                data[$"engine.{engineIndex}.state"] = state.ToString();
                data[$"engine.{engineIndex}.workerCount"] = workers.Count;

                for (int workerIndex = 0; workerIndex < workers.Count; workerIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    DatabaseEngineWorker worker = workers[workerIndex];
                    string prefix = $"engine.{engineIndex}.worker.{workerIndex}";
                    data[$"{prefix}.name"] = worker.Name;
                    data[$"{prefix}.kind"] = worker.Kind.ToString();
                    data[$"{prefix}.intervalMilliseconds"] = worker.Interval.TotalMilliseconds;
                    data[$"{prefix}.failureCount"] = worker.FailureCount;
                    workerCount++;

                    // A worker that keeps failing keeps running; its record says which, and what kind of
                    // failure (the event source carries the rest).
                    if (worker.Fault is { } fault)
                    {
                        string kind = DescribeFault(fault);
                        int consecutiveFailures = worker.ConsecutiveFailures;
                        data[$"{prefix}.consecutiveFailures"] = consecutiveFailures;
                        data[$"{prefix}.fault"] = kind;
                        failingWorkers.Add($"{worker.Name} ({consecutiveFailures} failed pass(es); {kind})");
                    }
                }

                switch (DatabaseHealthObservations.EngineStatus(state))
                {
                    case HealthStatus.Healthy:
                        break;
                    case HealthStatus.Degraded:
                        faultedEngines.Add(engine.Name);
                        break;
                    default:
                        unavailableEngines.Add(engine.Name);
                        break;
                }

                // A database that went offline refuses every request until it is reopened (#1243,
                // #1252, #1268, owner decision 25); the engine itself keeps running, so its state does
                // not show it. A database whose reopen failed is closed, and its engine no longer lists
                // it, so the reopen service's own list adds it. A disposed engine has no databases to
                // report.
                if (state != EngineState.Disposed)
                {
                    int databaseIndex = 0;
                    IReadOnlyList<DatabaseInstance> databases = engine.OpenDatabases;
                    IReadOnlyList<ReopenState> reopening = ReopenService?.GetPendingReopens() ?? [];
                    IEnumerable<DatabaseInstance> orderedDatabases = engine.Model is EngineModel.Document or EngineModel.Graph or EngineModel.Blob
                        ? databases.OrderBy(database => database.Name.ToString(), StringComparer.OrdinalIgnoreCase) : databases;
                    foreach (var database in orderedDatabases)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var progress = database.Provisioning;
                        bool paused = database.IsJournalAdmissionPaused;
                        observation?.ObserveDatabase(engine, database, progress, paused);
                        if (paused) { pausedDatabases.Add($"{engine.Name}/{database.Name}"); }
                        if (progress is null)
                        {
                            if (components?.WantsDatabase(engine.Name, database.Name.ToString()) == true)
                            {
                                components.Database(engine.Name, database.Name.ToString(), paused, null, null);
                            }
                            continue;
                        }
                        string progressPrefix = $"engine.{engineIndex}.provisioning.{databaseIndex++}";
                        data[$"{progressPrefix}.database"] = database.Name.ToString();
                        data[$"{progressPrefix}.operation"] = progress.Operation;
                        data[$"{progressPrefix}.object"] = progress.ObjectName;
                        data[$"{progressPrefix}.position"] = progress.Position;
                        data[$"{progressPrefix}.stepCount"] = progress.StepCount;
                        data[$"{progressPrefix}.state"] = progress.FailureCode is not null ? "Failed" : progress.IsRunning ? "Running" : "Pending";
                        if (progress.RowsProcessed is { } processed)
                        {
                            data[$"{progressPrefix}.rowsProcessed"] = processed;
                        }
                        if (progress.TotalRows is { } total)
                        {
                            data[$"{progressPrefix}.totalRows"] = total;
                        }
                        if (progress.FailureCode is { } code)
                        {
                            data[$"{progressPrefix}.failureCode"] = code;
                        }
                        string? detail = progress.Detail is { } refusal ? DatabaseHostingEventSource.BoundDetail(refusal) : null;
                        if (detail is not null) { data[$"{progressPrefix}.detail"] = detail; }
                        provisioningDatabases.Add($"{engine.Name}/{database.Name}: {progress.Operation} {progress.ObjectName}, " +
                            $"step {progress.Position} of {progress.StepCount}, " +
                            (progress.RowsProcessed is { } rows ? $"{rows} of {progress.TotalRows?.ToString() ?? "unknown"} rows" : "pending") +
                            (progress.FailureCode is { } failed ? $", {failed}" : string.Empty) +
                            (detail is not null ? $", {detail}" : string.Empty));
                        if (components?.WantsDatabase(engine.Name, database.Name.ToString()) == true)
                        {
                            var progressData = new Dictionary<string, object>(StringComparer.Ordinal);
                            string progressDataPrefix = progressPrefix + ".";
                            foreach (var entry in data)
                            {
                                if (entry.Key.StartsWith(progressDataPrefix, StringComparison.Ordinal))
                                {
                                    progressData.Add(entry.Key[(progressPrefix.Length + 1)..], entry.Value);
                                }
                            }
                            components.Database(engine.Name, database.Name.ToString(), paused, provisioningDatabases[^1], progressData);
                        }
                    }
                    data[$"engine.{engineIndex}.provisioningDatabaseCount"] = databaseIndex;
                    List<string> offline = [.. engine.OfflineDatabases.Select(name => (string)name)];
                    foreach (ReopenState pending in reopening)
                    {
                        if (ReferenceEquals(pending.Engine, engine) && !HasOpenDatabase(databases, pending.Name) && !offline.Contains(pending.Name, StringComparer.OrdinalIgnoreCase))
                        {
                            offline.Add(pending.Name);
                        }
                    }

                    data[$"engine.{engineIndex}.offlineDatabaseCount"] = offline.Count;
                    if (offline.Count > 0 && components is null)
                    {
                        data[$"engine.{engineIndex}.offlineDatabases"] = string.Join(", ", offline);
                    }

                    for (int offlineIndex = 0; offlineIndex < offline.Count; offlineIndex++)
                    {
                        string database = offline[offlineIndex];
                        string prefix = $"engine.{engineIndex}.offline.{offlineIndex}";
                        ReopenState? pending = HasOpenDatabase(databases, database) ? null : FindPending(reopening, engine, database);
                        StorageOfflineCause? cause = DatabaseReopenService.GetCause(engine, database) ?? pending?.Cause;
                        observation?.ObserveOffline(engine, database, cause);
                        data[$"{prefix}.name"] = database;
                        if (cause is { } known)
                        {
                            data[$"{prefix}.cause"] = known.ToString();
                        }

                        var detail = new List<string>(2);
                        if (cause is { } named)
                        {
                            detail.Add(named.ToString());
                        }

                        if (pending is { Attempts: > 0 } attempted)
                        {
                            data[$"{prefix}.reopenAttempts"] = attempted.Attempts;
                            if (attempted.LastFailure is { } failure)
                            {
                                string kind = DescribeFault(failure);
                                data[$"{prefix}.reopenFault"] = kind;
                                detail.Add($"{attempted.Attempts} failed reopen attempt(s), last {kind}");
                            }
                        }

                        offlineDatabases.Add(detail.Count == 0
                            ? $"{engine.Name}/{database}"
                            : $"{engine.Name}/{database} ({string.Join("; ", detail)})");
                        if (components?.WantsDatabase(engine.Name, database) == true)
                        {
                            var offlineData = new Dictionary<string, object>(StringComparer.Ordinal);
                            string offlineDataPrefix = prefix + ".";
                            foreach (var entry in data)
                            {
                                if (entry.Key.StartsWith(offlineDataPrefix, StringComparison.Ordinal))
                                {
                                    offlineData.Add(entry.Key[(prefix.Length + 1)..], entry.Value);
                                }
                            }
                            components.Offline(engine.Name, database, offlineDatabases[^1], offlineData);
                        }
                    }
                    int reportedOfflineCount = offline.Count;
                    foreach (var failure in engine.FailedOpens)
                    {
                        string database = failure.Name.ToString();
                        if (HasOpenDatabase(databases, database) || FindPending(reopening, engine, database) is not null) { continue; }
                        string prefix = $"engine.{engineIndex}.offline.{reportedOfflineCount++}";
                        string failedAt = failure.FailedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                        data[$"{prefix}.name"] = database;
                        data[$"{prefix}.reason"] = "Open failed";
                        data[$"{prefix}.reopenAttempts"] = failure.Attempts;
                        data[$"{prefix}.reopenFault"] = failure.ExceptionType;
                        data[$"{prefix}.failedAt"] = failedAt;
                        if (failure.FailureCode is { } code) { data[$"{prefix}.failureCode"] = code; }
                        string description = $"{engine.Name}/{database} (open failed; {failure.FailureCode}; {failure.ExceptionType}; {failedAt}; {failure.Attempts} attempt(s))";
                        offlineDatabases.Add(description);
                        if (components?.WantsDatabase(engine.Name, database) == true)
                        {
                            var detail = new Dictionary<string, object>(StringComparer.Ordinal)
                            {
                                ["reason"] = "Open failed",
                                ["reopenAttempts"] = failure.Attempts,
                                ["reopenFault"] = failure.ExceptionType,
                                ["failedAt"] = failedAt,
                            };
                            if (failure.FailureCode is { } failureCode) { detail["failureCode"] = failureCode; }
                            components.Offline(engine.Name, database, description, detail);
                        }
                    }
                    if (reportedOfflineCount != offline.Count)
                    {
                        data[$"engine.{engineIndex}.offlineDatabaseCount"] = reportedOfflineCount;
                    }
                }
                components?.Engine(engine.Name, state, data, $"engine.{engineIndex}.");
                ObjectDisposedException.ThrowIf(state != EngineState.Disposed && engine.State == EngineState.Disposed, engine);
            }
            // Component consumers fail closed per engine. HTTP retains its throwing-check policy.
            catch (Exception failure) when (components is not null && failure is not OutOfMemoryException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                components.Failure(engine.Name, DescribeFault(failure));
            }
        }

        // Components already carry their own descriptions; never build and discard an aggregate.
        if (components is not null) { return HealthCheckResult.Healthy(); }

        data["journalAdmissionPausedDatabaseCount"] = pausedDatabases.Count;
        if (pausedDatabases.Count > 0)
        {
            data["journalAdmissionPausedDatabases"] = string.Join(", ", pausedDatabases);
            data["journalAdmissionPauseCode"] = "CK06006";
        }
        data["workerCount"] = workerCount;
        data["offlineDatabaseCount"] = offlineDatabases.Count;
        data["provisioningDatabaseCount"] = provisioningDatabases.Count;
        data["reopenOfflineDatabases"] = ReopenService is not null;

        if (unavailableEngines.Count > 0)
        {
            return HealthCheckResult.Unhealthy(
                $"Unavailable database engines: {string.Join(", ", unavailableEngines)}.",
                data: data);
        }
        else if (offlineDatabases.Count > 0)
        {
            return HealthCheckResult.Unhealthy(
                $"Offline databases: {string.Join(", ", offlineDatabases)}. A device operation of their storage failed, or their " +
                "engine gave up on them after a background worker kept failing, so every operation on them is refused until " +
                "each is reopened. " + (ReopenService is not null
                    ? "The application reopens each by itself, with backoff, until the reopen succeeds; the reopen's recovery " +
                      "decides every commit that was not confirmed."
                    : "Reopen each (OpenDatabaseAsync), or restart the process to open them again; either runs recovery."),
                data: data);
        }
        else if (faultedEngines.Count > 0)
        {
            var description = new StringBuilder($"Database engines with worker faults: {string.Join(", ", faultedEngines)}.");
            if (failingWorkers.Count > 0)
            {
                description.Append(
                    $" Failing workers: {string.Join("; ", failingWorkers)}. They keep running and retry; each engine " +
                    "returns to Running once its workers complete the work their failures left.");
            }

            return HealthCheckResult.Degraded(description.ToString(), data: data);
        }
        else if (pausedDatabases.Count > 0)
        {
            return HealthCheckResult.Degraded(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                DatabaseHealthObservations.JournalAdmissionPauseDescription, $": {string.Join(", ", pausedDatabases)}"), data: data);
        }
        else if (provisioningDatabases.Count > 0)
        {
            return HealthCheckResult.Degraded($"Provisioning databases: {string.Join("; ", provisioningDatabases)}.", data: data);
        }
        else
        {
            return HealthCheckResult.Healthy(
                $"{engines.Count} database engine(s) running with {workerCount} worker(s).",
                data);
        }
    }

    /// <summary>
    /// Describes a worker's failure for the unauthenticated health output: its exception type, and
    /// the type of the failure underneath it when there is one. Never the message, which can carry
    /// file paths and storage internals.
    /// </summary>
    /// <param name="fault">The failure.</param>
    /// <returns>For example <c>StorageIOException (IOException)</c>.</returns>
    private static string DescribeFault(Exception fault)
        => fault.InnerException is { } inner
            ? $"{fault.GetType().Name} ({inner.GetType().Name})"
            : fault.GetType().Name;

    private static bool HasOpenDatabase(IReadOnlyList<DatabaseInstance> databases, string name)
    {
        foreach (var database in databases)
        {
            if (string.Equals(database.Name.ToString(), name, StringComparison.OrdinalIgnoreCase)) { return true; }
        }
        return false;
    }

    // The reopen service's record of a database, if it has one.
    private static ReopenState? FindPending(IReadOnlyList<ReopenState> reopening, DatabaseEngine engine, string database)
    {
        foreach (ReopenState pending in reopening)
        {
            if (ReferenceEquals(pending.Engine, engine) && string.Equals(pending.Name, database, StringComparison.OrdinalIgnoreCase))
            {
                return pending;
            }
        }

        return null;
    }

    /// <summary>
    /// Binds the composed host services once <see cref="DatabaseApplication"/> has
    /// built them (the context itself is created ahead of the application so
    /// deferred server factories can receive it).
    /// </summary>
    internal void SetHostedServices(IReadOnlyList<IHostService> hostedServices)
        => _hostedServices = hostedServices;

    internal void FreezeRegistries(
        IEnumerable<DatabaseEngine> engines,
        IEnumerable<DatabaseServer> servers)
    {
        _engines = Array.AsReadOnly([.. engines]);
        _servers = Array.AsReadOnly([.. servers]);
    }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Hosting.DatabaseApplicationContext _target;

        public DebugView(global::Assimalign.Cohesion.Database.Hosting.DatabaseApplicationContext value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Hosting.IHostEnvironment Environment => _target.Environment;
        public global::System.Collections.Generic.IEnumerable<global::Assimalign.Cohesion.Hosting.IHostService> HostedServices => _target.HostedServices;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.DatabaseEngine> Engines => _target.Engines;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.DatabaseServer> Servers => _target.Servers;
        public global::Assimalign.Cohesion.Configuration.IConfiguration Configuration => _target.Configuration;
        public global::System.IServiceProvider Services => _target.Services;
        public global::Assimalign.Cohesion.Hosting.HostId HostId => _target.HostId;
    }
}
