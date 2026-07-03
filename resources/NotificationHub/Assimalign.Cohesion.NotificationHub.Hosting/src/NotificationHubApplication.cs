using System;

namespace Assimalign.Cohesion.NotificationHub.Hosting;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.NotificationHub.Hosting.Internal;

/// <summary>
/// The standalone hosting application for the notification hub resource. Composes the resource's
/// units of work as hosted services, each selecting its execution model per the
/// Assimalign.Cohesion.Hosting per-service execution menu (see docs/DESIGN.md).
/// </summary>
public sealed class NotificationHubApplication : Host<NotificationHubApplicationContext>
{
    private readonly NotificationHubApplicationContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="NotificationHubApplication"/> class.
    /// </summary>
    /// <param name="options">The application options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public NotificationHubApplication(NotificationHubApplicationOptions options) : base(options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _context = new NotificationHubApplicationContext(options, new IHostService[]
        {
            new NotificationDispatchService(),
        });
    }

    /// <summary>
    /// Gets the application context.
    /// </summary>
    public override NotificationHubApplicationContext Context => _context;
}