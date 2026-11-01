using System.Collections.Generic;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.Tests;

/// <summary>
/// The plain host a test program runs its services in, as an area's application host does.
/// </summary>
internal sealed class ProgramHost : Host<ProgramHostContext>
{
    public ProgramHost(ProgramHostOptions options)
        : base(options)
    {
        Context = new ProgramHostContext(options.HostedServices);
    }

    public override ProgramHostContext Context { get; }
}

internal sealed class ProgramHostContext : HostContext
{
    public ProgramHostContext(IEnumerable<IHostService> services)
    {
        HostedServices = services;
        Environment = new HostEnvironment("Test");
    }

    public override IHostEnvironment Environment { get; }

    public override IEnumerable<IHostService> HostedServices { get; }
}

internal sealed class ProgramHostOptions : HostOptions<ProgramHostContext>
{
    public List<IHostService> HostedServices { get; } = [];
}
