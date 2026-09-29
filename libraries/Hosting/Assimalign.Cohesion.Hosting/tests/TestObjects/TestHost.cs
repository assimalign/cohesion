using System;
using System.Collections.Generic;
using System.IO;

namespace Assimalign.Cohesion.Hosting.Tests;

internal class TestHostContext : HostContext
{

    public TestHostContext(
        IEnumerable<IHostService> services,
        FileSystemPath? contentRootPath = null)
    {
        HostedServices = services;
        Environment = new HostEnvironment("Test")
        {
            ContentRootPath = contentRootPath,
        };
    }
    public override IHostEnvironment Environment { get; }
    public IServiceProvider? ServiceProvider { get; }
    public override IEnumerable<IHostService> HostedServices { get; }
}

internal class TestHostOptions : HostOptions<TestHostContext>
{
    public TestHostOptions()
    {
        HostedServices = new List<IHostService>();
        ContentRootPath = FileSystemPath.Parse(Path.GetFullPath(AppContext.BaseDirectory));
    }

    public List<IHostService> HostedServices { get; }

    public FileSystemPath? ContentRootPath { get; set; }
}

internal class TestHost : Host<TestHostContext>
{
    public TestHost(TestHostOptions options) : base(options)
    {
        Context = new TestHostContext(options.HostedServices, options.ContentRootPath);
    }

    public override TestHostContext Context { get; }
}
