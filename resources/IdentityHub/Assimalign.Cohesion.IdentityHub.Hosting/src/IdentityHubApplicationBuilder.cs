using System;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.IdentityHub;

namespace Assimalign.Cohesion.IdentityHub.Hosting;

internal sealed class IdentityHubApplicationBuilder : IIdentityHubApplicationBuilder
{
    internal IdentityHubApplicationBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
    }

    public IIdentityHubApplication Build()
    {
        var options = new IdentityHubApplicationOptions();
        var context = new IdentityHubApplicationContext();

        return new IdentityHubApplicationHost(options, context);
    }

    IHost IHostBuilder.Build() => Build();
}
