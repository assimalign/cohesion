using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Hosting;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Database.Hosting.Internal;

/// <summary>
/// The standalone hosting application for the database engine resource. Composes the
/// resource's units of work as hosted services, each selecting its execution model per
/// the <c>Assimalign.Cohesion.Hosting</c> per-service execution menu (see docs/DESIGN.md).
/// </summary>
/// <remarks>
/// Registration order is engines first, then the durability worker slots, then any
/// additional services, then the wire-protocol endpoint
/// (<see cref="DatabaseApplicationOptions.Server"/>). Because a host starts services
/// in registration order and stops them in reverse, engines are running before
/// anything pumps them and the endpoint starts last and stops first — connections
/// drain before the durability workers shut down, and the workers quiesce before the
/// engines perform their final durable flush.
/// </remarks>
public sealed class DatabaseApplication : Host<DatabaseApplicationContext>
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

        // Durability worker slots next: they own their threads for the host's whole
        // life and stop after the endpoint, so connections always drain ahead of them.
        if (options.EnableWriteAheadFlushService)
        {
            services.Add(new WriteAheadFlushService());
        }
        if (options.EnablePageWriterService)
        {
            services.Add(new PageWriterService());
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
}
