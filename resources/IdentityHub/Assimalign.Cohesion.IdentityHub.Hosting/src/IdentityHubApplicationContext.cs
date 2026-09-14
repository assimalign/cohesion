using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.IdentityHub.Hosting;

internal sealed class IdentityHubApplicationContext : HostContext
{
    private IReadOnlyList<IHostService> _hostedServices = Array.Empty<IHostService>();
    private readonly IHostEnvironment _environment;

    internal IdentityHubApplicationContext(string environmentName, System.IO.FileSystemPath? contentRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);

        // The opt-in resource runner asserts that the host content root equals the ambient resource content root,
        // so an enabled resource seeds it from the ambient context; a plain application keeps it unset.
        _environment = new HostEnvironment(environmentName) { ContentRootPath = contentRootPath };
    }

    public override IHostEnvironment Environment => _environment;

    public override IEnumerable<IHostService> HostedServices => _hostedServices;

    internal void SetHostedServices(IReadOnlyList<IHostService> hostedServices)
    {
        ArgumentNullException.ThrowIfNull(hostedServices);
        _hostedServices = hostedServices;
    }
}
