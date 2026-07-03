using System;

namespace Assimalign.Cohesion.MediaHub.Hosting;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.MediaHub.Hosting.Internal;

/// <summary>
/// The standalone hosting application for the media hub resource. Composes the resource's
/// units of work as hosted services, each selecting its execution model per the
/// Assimalign.Cohesion.Hosting per-service execution menu (see docs/DESIGN.md).
/// </summary>
public sealed class MediaHubApplication : Host<MediaHubApplicationContext>
{
    private readonly MediaHubApplicationContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaHubApplication"/> class.
    /// </summary>
    /// <param name="options">The application options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public MediaHubApplication(MediaHubApplicationOptions options) : base(options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _context = new MediaHubApplicationContext(options, new IHostService[]
        {
            new ContentIoService(),
            new StreamingEndpointService(),
        });
    }

    /// <summary>
    /// Gets the application context.
    /// </summary>
    public override MediaHubApplicationContext Context => _context;
}