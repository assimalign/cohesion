
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting;

using Assimalign.Cohesion.Net.WebSockets;

public static class HostBuilderExtensions
{
    public static IHostBuilder AddWebSocketServer(this IHostBuilder builder, Action<WebSocketServerBuilder> configure)
    {
        if (configure is null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        var serverBuilder = new WebSocketServerBuilder();

        configure.Invoke(serverBuilder);

        return builder.AddService(((IHostServiceBuilder)serverBuilder).Build());
    }
}
