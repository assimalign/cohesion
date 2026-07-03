using System;

namespace Assimalign.Cohesion.LogSpace.Hosting;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.LogSpace.Hosting.Internal;

/// <summary>
/// The standalone hosting application for the log storage engine resource. Composes the resource's
/// units of work as hosted services, each selecting its execution model per the
/// Assimalign.Cohesion.Hosting per-service execution menu (see docs/DESIGN.md).
/// </summary>
public sealed class LogSpaceApplication : Host<LogSpaceApplicationContext>
{
    private readonly LogSpaceApplicationContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogSpaceApplication"/> class.
    /// </summary>
    /// <param name="options">The application options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public LogSpaceApplication(LogSpaceApplicationOptions options) : base(options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _context = new LogSpaceApplicationContext(options, new IHostService[]
        {
            new SegmentFlushService(),
            new IngestEndpointService(),
        });
    }

    /// <summary>
    /// Gets the application context.
    /// </summary>
    public override LogSpaceApplicationContext Context => _context;
}