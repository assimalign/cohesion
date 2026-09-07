using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.MediaHub.Hosting;

internal sealed class MediaHubApplicationContext : HostContext
{
    private static readonly IReadOnlyList<IHostService> _hostedServices = Array.Empty<IHostService>();
    private readonly IHostEnvironment _environment = new HostEnvironment("production");

    public override IHostEnvironment Environment => _environment;

    public override IEnumerable<IHostService> HostedServices => _hostedServices;
}
