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
/// Registration order is durability workers first, then the composed endpoint (and any
/// other) services. Because a host starts services in registration order and stops them
/// in reverse, endpoints start last and stop first — connections drain before the
/// durability workers shut down.
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

        // Durability worker slots first: they own their threads for the host's whole
        // life and stop last, so an endpoint always drains ahead of them.
        if (options.EnableWriteAheadFlushService)
        {
            services.Add(new WriteAheadFlushService());
        }
        if (options.EnablePageWriterService)
        {
            services.Add(new PageWriterService());
        }

        // Then the composition root's endpoint (and any other) services — typically the
        // wire-protocol server via DatabaseServer.CreateHostService.
        foreach (IHostService service in options.Services)
        {
            services.Add(service);
        }

        _context = new DatabaseApplicationContext(options, services);
    }

    /// <summary>
    /// Gets the application context.
    /// </summary>
    public override DatabaseApplicationContext Context => _context;
}
