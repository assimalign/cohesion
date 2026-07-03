using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.NatGateway.Hosting;

using Assimalign.Cohesion.Hosting;

/// <summary>
/// The host context for <see cref="NatGatewayApplication"/>.
/// </summary>
public sealed class NatGatewayApplicationContext : HostContext
{
    private readonly IHostEnvironment _environment;
    private readonly IReadOnlyList<IHostService> _hostedServices;

    internal NatGatewayApplicationContext(NatGatewayApplicationOptions options, IReadOnlyList<IHostService> hostedServices)
    {
        _environment = new HostEnvironment(options.Environment ?? "production");
        _hostedServices = hostedServices;
    }

    /// <summary>
    /// Gets the host environment information.
    /// </summary>
    public override IHostEnvironment Environment => _environment;

    /// <summary>
    /// Gets the hosted services composed by the application.
    /// </summary>
    public override IEnumerable<IHostService> HostedServices => _hostedServices;
}