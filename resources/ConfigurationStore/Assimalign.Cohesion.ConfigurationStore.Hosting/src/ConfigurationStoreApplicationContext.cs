using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting;

internal sealed class ConfigurationStoreApplicationContext : HostContext
{
    private IReadOnlyList<IHostService> _hostedServices = Array.Empty<IHostService>();
    private readonly IHostEnvironment _environment;

    internal ConfigurationStoreApplicationContext(string environmentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        _environment = new HostEnvironment(environmentName);
    }

    public override IHostEnvironment Environment => _environment;

    public override IEnumerable<IHostService> HostedServices => _hostedServices;

    internal void SetHostedServices(IReadOnlyList<IHostService> hostedServices)
    {
        ArgumentNullException.ThrowIfNull(hostedServices);
        _hostedServices = hostedServices;
    }
}
