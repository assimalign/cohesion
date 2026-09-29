using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Configuration;

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
    private readonly IHostEnvironment _environment;
    private IReadOnlyList<IDatabaseEngine> _engines;
    private IReadOnlyList<IDatabaseServer> _servers;
    private IReadOnlyList<IHostService> _hostedServices = [];

    internal DatabaseApplicationContext(DatabaseApplicationOptions options, IConfiguration configuration, IServiceProvider services)
    {
        Configuration = configuration;
        Services = services;
        _environment = new HostEnvironment(options.Environment ?? "production")
        {
            ContentRootPath = options.ContentRootPath,
        };
        _engines = new ReadOnlyCollection<IDatabaseEngine>(options.Engines);
        _servers = new ReadOnlyCollection<IDatabaseServer>(options.Servers);
    }

    /// <summary>
    /// Gets the host environment information.
    /// </summary>
    public override IHostEnvironment Environment => _environment;

    /// <summary>
    /// Gets the hosted services composed by the application.
    /// </summary>
    public override IEnumerable<IHostService> HostedServices => _hostedServices;

    /// <inheritdoc />
    public IReadOnlyList<IDatabaseEngine> Engines => _engines;

    /// <inheritdoc />
    public IReadOnlyList<IDatabaseServer> Servers => _servers;

    /// <summary>Gets loaded configuration; mutations do not recompose engine options.</summary>
    public IConfiguration Configuration { get; }

    /// <summary>Gets the application provider, borrowed until application disposal.</summary>
    public IServiceProvider Services { get; }

    /// <inheritdoc />
    public IDatabaseEngine GetEngine(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        foreach (IDatabaseEngine engine in _engines)
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
    /// Aggregates the state and worker inventory of every distinct registered or server-fronted
    /// database engine.
    /// </summary>
    /// <param name="cancellationToken">Signals that the health evaluation should be abandoned.</param>
    /// <returns>
    /// A healthy result when every engine is running, a degraded result when an engine reports a
    /// worker fault, or an unhealthy result when an engine is disposed or reports an unknown state.
    /// </returns>
    public ValueTask<HealthContribution> CheckAsync(CancellationToken cancellationToken = default)
    {
        var engines = new List<IDatabaseEngine>(_engines.Count + _servers.Count);
        var observedEngines = new HashSet<IDatabaseEngine>(ReferenceEqualityComparer.Instance);

        foreach (IDatabaseEngine engine in _engines)
        {
            if (observedEngines.Add(engine))
            {
                engines.Add(engine);
            }
        }

        foreach (IDatabaseServer server in _servers)
        {
            IDatabaseEngine engine = server.Context.Engine;
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
        var unavailableEngines = new List<string>();
        int workerCount = 0;

        for (int engineIndex = 0; engineIndex < engines.Count; engineIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IDatabaseEngine engine = engines[engineIndex];
            EngineState state = engine.State;
            IReadOnlyList<IDatabaseEngineWorker> workers = engine.Workers;

            data[$"engine.{engineIndex}.name"] = engine.Name;
            data[$"engine.{engineIndex}.model"] = engine.Model.ToString();
            data[$"engine.{engineIndex}.state"] = state.ToString();
            data[$"engine.{engineIndex}.workerCount"] = workers.Count;

            for (int workerIndex = 0; workerIndex < workers.Count; workerIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IDatabaseEngineWorker worker = workers[workerIndex];
                string prefix = $"engine.{engineIndex}.worker.{workerIndex}";
                data[$"{prefix}.name"] = worker.Name;
                data[$"{prefix}.kind"] = worker.Kind.ToString();
                data[$"{prefix}.intervalMilliseconds"] = worker.Interval.TotalMilliseconds;
                workerCount++;
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
        }

        data["workerCount"] = workerCount;

        HealthContribution contribution;
        if (unavailableEngines.Count > 0)
        {
            contribution = HealthContribution.Unhealthy(
                $"Unavailable database engines: {string.Join(", ", unavailableEngines)}.",
                data);
        }
        else if (faultedEngines.Count > 0)
        {
            contribution = HealthContribution.Degraded(
                $"Database engines with worker faults: {string.Join(", ", faultedEngines)}.",
                data);
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
    /// Binds the composed host services once <see cref="DatabaseApplication"/> has
    /// built them (the context itself is created ahead of the application so
    /// deferred server factories can receive it).
    /// </summary>
    internal void SetHostedServices(IReadOnlyList<IHostService> hostedServices)
        => _hostedServices = hostedServices;

    internal void FreezeRegistries(
        IEnumerable<IDatabaseEngine> engines,
        IEnumerable<IDatabaseServer> servers)
    {
        _engines = Array.AsReadOnly([.. engines]);
        _servers = Array.AsReadOnly([.. servers]);
    }
}
