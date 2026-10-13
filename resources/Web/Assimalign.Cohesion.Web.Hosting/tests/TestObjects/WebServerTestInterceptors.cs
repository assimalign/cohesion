using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;

namespace Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

internal static class WebServerTestInterceptors
{
    public static async Task InstallAsync(HttpConnectionListenerOptions options)
    {
        // Capture the exact hosted composition while the public server constructs its listener.
        // The temporary unbound application owns only its own transport, so disposing it cannot
        // consume or close the raw connection the caller uses to inspect ReceiveAsync.
        await using InMemoryConnectionListener transport = new();
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        IReadOnlyList<IHttpExchangeInterceptor>? snapshot = null;
        builder.Server.UseServer(configured => configured.UseHttp1(() =>
        {
            snapshot = configured.Interceptors.ToArray();
            return transport;
        }));
        await using WebApplication application = builder.Build();
        IWebApplicationServer server = application.Context.Servers.Single();

        try
        {
            foreach (IHttpExchangeInterceptor interceptor in snapshot!)
            {
                options.Interceptors.Add(interceptor);
            }
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }
}
