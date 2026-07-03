using System;

namespace Assimalign.Cohesion.VpnGateway.Hosting;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.VpnGateway.Hosting.Internal;

/// <summary>
/// The standalone hosting application for the VPN gateway resource. Composes the resource's
/// units of work as hosted services, each selecting its execution model per the
/// Assimalign.Cohesion.Hosting per-service execution menu (see docs/DESIGN.md).
/// </summary>
public sealed class VpnGatewayApplication : Host<VpnGatewayApplicationContext>
{
    private readonly VpnGatewayApplicationContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="VpnGatewayApplication"/> class.
    /// </summary>
    /// <param name="options">The application options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public VpnGatewayApplication(VpnGatewayApplicationOptions options) : base(options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _context = new VpnGatewayApplicationContext(options, new IHostService[]
        {
            new TunnelDataPlaneService(),
        });
    }

    /// <summary>
    /// Gets the application context.
    /// </summary>
    public override VpnGatewayApplicationContext Context => _context;
}