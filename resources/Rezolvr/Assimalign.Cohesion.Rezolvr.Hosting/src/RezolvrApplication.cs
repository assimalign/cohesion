using System;

namespace Assimalign.Cohesion.Rezolvr.Hosting;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Rezolvr.Hosting.Internal;

/// <summary>
/// The standalone hosting application for the name resolver resource. Composes the resource's
/// units of work as hosted services, each selecting its execution model per the
/// Assimalign.Cohesion.Hosting per-service execution menu (see docs/DESIGN.md).
/// </summary>
public sealed class RezolvrApplication : Host<RezolvrApplicationContext>
{
    private readonly RezolvrApplicationContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="RezolvrApplication"/> class.
    /// </summary>
    /// <param name="options">The application options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public RezolvrApplication(RezolvrApplicationOptions options) : base(options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _context = new RezolvrApplicationContext(options, new IHostService[]
        {
            new ResolverEndpointService(),
        });
    }

    /// <summary>
    /// Gets the application context.
    /// </summary>
    public override RezolvrApplicationContext Context => _context;
}