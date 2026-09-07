using System;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.MessageHub;

namespace Assimalign.Cohesion.MessageHub.Hosting;

internal sealed class MessageHubApplicationBuilder : IMessageHubApplicationBuilder
{
    internal MessageHubApplicationBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
    }

    public IMessageHubApplication Build()
    {
        var options = new MessageHubApplicationOptions();
        var context = new MessageHubApplicationContext();

        return new MessageHubApplicationHost(options, context);
    }

    IHost IHostBuilder.Build() => Build();
}
