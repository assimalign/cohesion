using System;

namespace Assimalign.Cohesion.NatGateway.Hosting;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.NatGateway.Hosting.Internal;

/// <summary>
/// The standalone hosting application for the NAT gateway resource. Composes the resource's
/// units of work as hosted services, each selecting its execution model per the
/// Assimalign.Cohesion.Hosting per-service execution menu (see docs/DESIGN.md).
/// </summary>
public sealed class NatGatewayApplication : Host<NatGatewayApplicationContext>
{
    private readonly NatGatewayApplicationContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="NatGatewayApplication"/> class.
    /// </summary>
    /// <param name="options">The application options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public NatGatewayApplication(NatGatewayApplicationOptions options) : base(options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _context = new NatGatewayApplicationContext(options, new IHostService[]
        {
            new TranslationDataPlaneService(),
        });
    }

    /// <summary>
    /// Gets the application context.
    /// </summary>
    public override NatGatewayApplicationContext Context => _context;
}