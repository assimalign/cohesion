using System;

namespace Assimalign.Cohesion.EventHub.Hosting;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.EventHub.Hosting.Internal;

/// <summary>
/// The standalone hosting application for the event streaming hub resource. Composes the resource's
/// units of work as hosted services, each selecting its execution model per the
/// Assimalign.Cohesion.Hosting per-service execution menu (see docs/DESIGN.md).
/// </summary>
public sealed class EventHubApplication : Host<EventHubApplicationContext>
{
    private readonly EventHubApplicationContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="EventHubApplication"/> class.
    /// </summary>
    /// <param name="options">The application options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public EventHubApplication(EventHubApplicationOptions options) : base(options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _context = new EventHubApplicationContext(options, new IHostService[]
        {
            new PartitionFlushService(),
            new IngressEndpointService(),
        });
    }

    /// <summary>
    /// Gets the application context.
    /// </summary>
    public override EventHubApplicationContext Context => _context;
}