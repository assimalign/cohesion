// This project makes sink-signature drift fail here instead of in consumer projects.
// Every side of every declared component-integration pair must be referenced here.

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.IntegrationCheck;

internal static class IntegrationSurface
{
    internal static void Configure(IServiceProviderBuilder builder)
    {
        builder.AddHttpClientFactory(clients => clients.AddClient("canary", options => { }));
    }
}
