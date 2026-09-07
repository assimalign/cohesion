using System;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.IoTHub;

namespace Assimalign.Cohesion.IoTHub.Hosting;

internal sealed class IoTHubApplicationBuilder : IIoTHubApplicationBuilder
{
    internal IoTHubApplicationBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
    }

    public IIoTHubApplication Build()
    {
        var options = new IoTHubApplicationOptions();
        var context = new IoTHubApplicationContext();

        return new IoTHubApplicationHost(options, context);
    }

    IHost IHostBuilder.Build() => Build();
}
