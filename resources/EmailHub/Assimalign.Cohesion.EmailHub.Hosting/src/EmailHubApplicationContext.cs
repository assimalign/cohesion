using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.EmailHub.Hosting;

using Assimalign.Cohesion.Hosting;

/// <summary>
/// The host context for <see cref="EmailHubApplication"/>.
/// </summary>
public sealed class EmailHubApplicationContext : HostContext
{
    private readonly IHostEnvironment _environment;
    private readonly IReadOnlyList<IHostService> _hostedServices;

    internal EmailHubApplicationContext(EmailHubApplicationOptions options, IReadOnlyList<IHostService> hostedServices)
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