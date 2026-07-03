using System;

namespace Assimalign.Cohesion.Database.Hosting;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Database.Hosting.Internal;

/// <summary>
/// The standalone hosting application for the database engine resource. Composes the resource's
/// units of work as hosted services, each selecting its execution model per the
/// Assimalign.Cohesion.Hosting per-service execution menu (see docs/DESIGN.md).
/// </summary>
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

        _context = new DatabaseApplicationContext(options, new IHostService[]
        {
            new WriteAheadFlushService(),
            new PageWriterService(),
            new QueryEndpointService(),
        });
    }

    /// <summary>
    /// Gets the application context.
    /// </summary>
    public override DatabaseApplicationContext Context => _context;
}