using System;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.NotificationHub;

namespace Assimalign.Cohesion.NotificationHub.Hosting;

internal sealed class NotificationHubApplicationBuilder : INotificationHubApplicationBuilder
{
    internal NotificationHubApplicationBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
    }

    public INotificationHubApplication Build()
    {
        var options = new NotificationHubApplicationOptions();
        var context = new NotificationHubApplicationContext();

        return new NotificationHubApplicationHost(options, context);
    }

    IHost IHostBuilder.Build() => Build();
}
