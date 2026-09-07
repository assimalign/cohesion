using System;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.EventHub;

namespace Assimalign.Cohesion.EventHub.Hosting;

internal sealed class EventHubApplicationBuilder : IEventHubApplicationBuilder
{
    internal EventHubApplicationBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
    }

    public IEventHubApplication Build()
    {
        var options = new EventHubApplicationOptions();
        var context = new EventHubApplicationContext();

        return new EventHubApplicationHost(options, context);
    }

    IHost IHostBuilder.Build() => Build();
}
