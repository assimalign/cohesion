using System;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.EmailHub;

namespace Assimalign.Cohesion.EmailHub.Hosting;

internal sealed class EmailHubApplicationBuilder : IEmailHubApplicationBuilder
{
    internal EmailHubApplicationBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
    }

    public IEmailHubApplication Build()
    {
        var options = new EmailHubApplicationOptions();
        var context = new EmailHubApplicationContext();

        return new EmailHubApplicationHost(options, context);
    }

    IHost IHostBuilder.Build() => Build();
}
