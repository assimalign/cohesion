using System;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.MediaHub;

namespace Assimalign.Cohesion.MediaHub.Hosting;

internal sealed class MediaHubApplicationBuilder : IMediaHubApplicationBuilder
{
    internal MediaHubApplicationBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
    }

    public IMediaHubApplication Build()
    {
        var options = new MediaHubApplicationOptions();
        var context = new MediaHubApplicationContext();

        return new MediaHubApplicationHost(options, context);
    }

    IHost IHostBuilder.Build() => Build();
}
