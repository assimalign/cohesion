using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.MessageHub.Hosting;

internal sealed class MessageHubApplicationContext : HostContext
{
    private IReadOnlyList<IHostService> _hostedServices = Array.Empty<IHostService>();
    private readonly IHostEnvironment _environment = new HostEnvironment("production");

    public override IHostEnvironment Environment => _environment;

    public override IEnumerable<IHostService> HostedServices => _hostedServices;

    internal void SetHostedServices(IHostService[] hostedServices)
    {
        ArgumentNullException.ThrowIfNull(hostedServices);

        _hostedServices = Array.AsReadOnly(hostedServices);
    }
}
