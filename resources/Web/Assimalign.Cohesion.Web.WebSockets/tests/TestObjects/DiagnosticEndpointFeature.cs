using System.Threading.Tasks;

using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.WebSockets.Tests.TestObjects;

internal sealed class DiagnosticEndpointFeature : IRouterEndpointFeature
{
    public int ReadCount { get; private set; }

    public string Name => "DiagnosticEndpoint";

    public WebApplicationMiddleware Endpoint => _ => Task.CompletedTask;

    public string RouteTemplate
    {
        get
        {
            ReadCount++;
            return "/chat/{room}";
        }
    }
}
