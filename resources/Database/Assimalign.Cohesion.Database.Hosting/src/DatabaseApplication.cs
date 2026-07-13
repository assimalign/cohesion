using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Hosting;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Database.Hosting.Internal;

/// <summary>
/// The standalone hosting application for the database engine resource. Composes the
/// resource's units of work as hosted services, each selecting its execution model per
/// the <c>Assimalign.Cohesion.Hosting</c> per-service execution menu (see docs/DESIGN.md).
/// </summary>
/// <remarks>
/// Registration order is engines first, then the claimed engine-worker slots
/// (<see cref="DatabaseApplicationOptions.Workers"/>), then any additional services,
/// then the wire-protocol endpoint (<see cref="DatabaseApplicationOptions.Server"/>).
/// Because a host starts services in registration order and stops them in reverse,
/// engines are running before anything pumps them and the endpoint starts last and
/// stops first — connections drain before the worker slots shut down, and the
/// workers quiesce before the engines perform their final durable flush.
/// Compose one through <see cref="CreateBuilder()"/> (the builder-first surface —
/// model packages register their engines on the root's
/// <see cref="IDatabaseApplicationBuilder"/> seam) or construct it directly from
/// fully populated <see cref="DatabaseApplicationOptions"/>.
/// </remarks>
public sealed class DatabaseApplication : Host<DatabaseApplicationContext>, IDatabaseApplication
{
    private readonly DatabaseApplicationContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseApplication"/> class.
    /// </summary>
    /// <param name="options">The application options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public DatabaseApplication(DatabaseApplicationOptions options) : base(options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var services = new List<IHostService>();

        // Engines register first: they start before every worker slot and the endpoint,
        // and stop last — after the endpoint has drained and the worker slots have
        // quiesced — so the engine's own stop performs the final durable flush.
        foreach (IDatabaseEngine engine in options.Engines)
        {
            services.Add(new DatabaseEngineHostService(engine));
        }

        // Worker slots next: claim each engine-owned worker whose slot is enabled and
        // map it onto the configured execution-menu member. Claims are taken here, at
        // composition time and before the engines start, so an engine never
        // self-schedules a worker the host drives (single ownership). A worker whose
        // claim fails — the engine was started before this composition and already
        // self-scheduled it — stays with the engine; a disabled slot likewise hands
        // the loop back to the engine's own scheduler. Either way the work runs.
        foreach (IDatabaseEngine engine in options.Engines)
        {
            foreach (IDatabaseEngineWorker worker in engine.Workers)
            {
                DatabaseWorkerSlotOptions slot = options.Workers.GetSlot(worker.Kind);

                if (!slot.Enabled || !worker.TryClaim())
                {
                    continue;
                }

                services.Add(CreateWorkerService(worker, slot.Execution));
            }
        }

        // Then the composition root's additional services.
        foreach (IHostService service in options.Services)
        {
            services.Add(service);
        }

        // The wire-protocol endpoint registers last: a host starts services in
        // registration order and stops them in reverse, so the endpoint starts
        // last and drains first.
        if (options.Server is not null)
        {
            services.Add(new DatabaseServerHostService(options.Server));
        }

        _context = new DatabaseApplicationContext(options, services);
    }

    /// <summary>
    /// Gets the application context.
    /// </summary>
    public override DatabaseApplicationContext Context => _context;

    /// <summary>
    /// Gets the database engines this application serves, in registration order.
    /// </summary>
    public IReadOnlyList<IDatabaseEngine> Engines => _context.Engines;

    /// <summary>
    /// Creates a builder for composing a database application — the entry point of
    /// the area's builder pattern (mirrors <c>WebApplication.CreateBuilder()</c>).
    /// Model packages register their engines on the returned builder through the
    /// root's <see cref="IDatabaseApplicationBuilder"/> seam.
    /// </summary>
    /// <returns>A new application builder over default options.</returns>
    public static DatabaseApplicationBuilder CreateBuilder()
    {
        return CreateBuilder(new DatabaseApplicationOptions());
    }

    /// <summary>
    /// Creates a builder for composing a database application over the specified
    /// options.
    /// </summary>
    /// <param name="options">The application options the builder composes into.</param>
    /// <returns>A new application builder over <paramref name="options"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public static DatabaseApplicationBuilder CreateBuilder(DatabaseApplicationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new DatabaseApplicationBuilder(options);
    }

    Task IDatabaseApplication.StartAsync(CancellationToken cancellationToken)
    {
        return ((IHost)this).StartAsync(cancellationToken);
    }

    Task IDatabaseApplication.StopAsync(CancellationToken cancellationToken)
    {
        return ((IHost)this).StopAsync(cancellationToken);
    }

    /// <summary>
    /// Wraps a claimed engine worker in the host service matching the configured
    /// execution-menu member, preferring the named per-kind service types so worker
    /// threads are recognizable in dumps.
    /// </summary>
    private static IHostService CreateWorkerService(IDatabaseEngineWorker worker, DatabaseWorkerExecution execution)
    {
        if (execution == DatabaseWorkerExecution.DedicatedThread)
        {
            return worker.Kind switch
            {
                DatabaseEngineWorkerKind.WriteAheadFlush => new WriteAheadFlushService(worker),
                DatabaseEngineWorkerKind.PageWriteBack => new PageWriterService(worker),
                _ => new DatabaseWorkerThreadService(worker),
            };
        }

        return worker.Kind switch
        {
            DatabaseEngineWorkerKind.Checkpoint => new CheckpointService(worker),
            _ => new DatabaseWorkerTimerService(worker),
        };
    }
}
