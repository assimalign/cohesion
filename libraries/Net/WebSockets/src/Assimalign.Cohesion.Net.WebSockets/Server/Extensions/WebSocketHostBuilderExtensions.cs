
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.WebSockets;

using Assimalign.Cohesion.Net.Hosting;

public static class WebSocketHostBuilderExtensions
{
    public static IHostBuilder AddWebSocketServer(this IHostBuilder builder, Action<WebSocketServerBuilder> configure)
    {
        if (configure is null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        var serverBuilder = new WebSocketServerBuilder();

        configure.Invoke(serverBuilder);

        return builder.AddServer(serverBuilder);
    }
}
