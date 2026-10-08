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

namespace Assimalign.Cohesion.Database.Hosting;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Health;

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
public sealed class DatabaseApplicationContext : HostContext, IDatabaseApplicationContext, IHealthContributor
{
    private readonly HostEnvironment _environment;
    private IReadOnlyList<DatabaseEngine> _engines;
    private IReadOnlyList<DatabaseServer> _servers;
    private IReadOnlyList<IHostService> _hostedServices = [];

    internal DatabaseApplicationContext(
        DatabaseApplicationOptions options,
        HostEnvironment environment,
        ConfigurationManager configuration,
        ServiceProvider services,
        LoggerFactory loggerFactory)
    {
        Configuration = configuration;
        Services = services;
        _environment = environment;
        BuildContext = new DatabaseApplicationBuildContext(environment, configuration, services, loggerFactory);
        _engines = new ReadOnlyCollection<DatabaseEngine>(options.Engines);
        _servers = new ReadOnlyCollection<DatabaseServer>(options.Servers);
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
        throw new KeyNotFoundException($"No database engine named '{name}' is registered.");
    }

    /// <summary>
    /// Gets the stable name of the database application health contribution.
    /// </summary>
    public string Name => "database";

    /// <summary>
    /// Gets the application's reopen service (owner decision 22), or null when
    /// <see cref="DatabaseApplicationOptions.ReopenOfflineDatabases"/> is off. Set once at Build.
    /// </summary>
    internal DatabaseReopenService? ReopenService { get; set; }

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
    public ValueTask<HealthContribution> CheckAsync(CancellationToken cancellationToken = default)
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
        int workerCount = 0;

        // The databases the application is reopening: a failed reopen left them closed, so their
        // engine no longer lists them, but they are not open again (owner decision 22).
        IReadOnlyList<ReopenState> reopening = ReopenService?.GetPendingReopens() ?? [];

        for (int engineIndex = 0; engineIndex < engines.Count; engineIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DatabaseEngine engine = engines[engineIndex];
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

            switch (state)
            {
                case EngineState.Running:
                    break;
                case EngineState.Faulted:
                    faultedEngines.Add(engine.Name);
                    break;
                case EngineState.Disposed:
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
                List<string> offline = [.. engine.OfflineDatabases.Select(name => (string)name)];
                foreach (ReopenState pending in reopening)
                {
                    if (ReferenceEquals(pending.Engine, engine) && !offline.Contains(pending.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        offline.Add(pending.Name);
                    }
                }

                data[$"engine.{engineIndex}.offlineDatabaseCount"] = offline.Count;
                if (offline.Count > 0)
                {
                    data[$"engine.{engineIndex}.offlineDatabases"] = string.Join(", ", offline);
                }

                for (int offlineIndex = 0; offlineIndex < offline.Count; offlineIndex++)
                {
                    string database = offline[offlineIndex];
                    string prefix = $"engine.{engineIndex}.offline.{offlineIndex}";
                    ReopenState? pending = FindPending(reopening, engine, database);
                    StorageOfflineCause? cause = DatabaseReopenService.GetCause(engine, database) ?? pending?.Cause;
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
                }
            }
        }

        data["workerCount"] = workerCount;
        data["offlineDatabaseCount"] = offlineDatabases.Count;
        data["reopenOfflineDatabases"] = ReopenService is not null;

        HealthContribution contribution;
        if (unavailableEngines.Count > 0)
        {
            contribution = HealthContribution.Unhealthy(
                $"Unavailable database engines: {string.Join(", ", unavailableEngines)}.",
                data);
        }
        else if (offlineDatabases.Count > 0)
        {
            contribution = HealthContribution.Unhealthy(
                $"Offline databases: {string.Join(", ", offlineDatabases)}. A device operation of their storage failed, or their " +
                "engine gave up on them after a background worker kept failing, so every operation on them is refused until " +
                "each is reopened. " + (ReopenService is not null
                    ? "The application reopens each by itself, with backoff, until the reopen succeeds; the reopen's recovery " +
                      "decides every commit that was not confirmed."
                    : "Reopen each (OpenDatabaseAsync), or restart the process to open them again; either runs recovery."),
                data);
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

            contribution = HealthContribution.Degraded(description.ToString(), data);
        }
        else
        {
            contribution = HealthContribution.Healthy(
                $"{engines.Count} database engine(s) running with {workerCount} worker(s).",
                data);
        }

        return ValueTask.FromResult(contribution);
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
}
