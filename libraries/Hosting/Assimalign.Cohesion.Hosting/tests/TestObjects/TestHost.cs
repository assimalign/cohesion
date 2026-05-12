using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Hosting.Tests;

internal class TestHostContext : HostContext
{

    public TestHostContext(IEnumerable<IHostService> services)
    {
        HostedServices = services;
        Environment = new HostEnvironment("Test");
    }
    public override IHostEnvironment Environment { get; }
    public override IServiceProvider? ServiceProvider { get; }
    public override IEnumerable<IHostService> HostedServices { get; }
}

internal class TestHostOptions : HostOptions<TestHostContext>
{
    public TestHostOptions()
    {
        HostedServices = new List<IHostService>();
    }

    public List<IHostService> HostedServices { get; }
}

internal class TestHost : Host<TestHostContext>
{
    public TestHost(TestHostOptions options) : base(options)
    {
        Context = new TestHostContext(options.HostedServices);
    }

    public override TestHostContext Context { get; }
}
