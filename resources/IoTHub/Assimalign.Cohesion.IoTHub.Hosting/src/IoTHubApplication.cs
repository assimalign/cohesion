using System;

namespace Assimalign.Cohesion.IoTHub.Hosting;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.IoTHub.Hosting.Internal;

/// <summary>
/// The standalone hosting application for the IoT device hub resource. Composes the resource's
/// units of work as hosted services, each selecting its execution model per the
/// Assimalign.Cohesion.Hosting per-service execution menu (see docs/DESIGN.md).
/// </summary>
public sealed class IoTHubApplication : Host<IoTHubApplicationContext>
{
    private readonly IoTHubApplicationContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="IoTHubApplication"/> class.
    /// </summary>
    /// <param name="options">The application options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public IoTHubApplication(IoTHubApplicationOptions options) : base(options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _context = new IoTHubApplicationContext(options, new IHostService[]
        {
            new TelemetryJournalService(),
            new DeviceIngressService(),
        });
    }

    /// <summary>
    /// Gets the application context.
    /// </summary>
    public override IoTHubApplicationContext Context => _context;
}