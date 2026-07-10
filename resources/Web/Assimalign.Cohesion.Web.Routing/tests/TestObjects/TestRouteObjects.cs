using System.Collections.Generic;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing.Metadata;
using Assimalign.Cohesion.Web.Routing.Patterns;

namespace Assimalign.Cohesion.Web.Routing.Tests.TestObjects;

/// <summary>
/// An <see cref="IRouterRoute"/> without a <see cref="RoutePattern"/>, standing in for a fully
/// custom matcher that is not addressable by outbound URL generation.
/// </summary>
internal sealed class PatternlessRoute : IRouterRoute
{
    public PatternlessRoute(IRouterRouteMetadataCollection? metadata = null)
    {
        Metadata = metadata ?? RouterRouteMetadataCollection.Empty;
    }

    public IRouterRouteHandler Handler { get; } = new RecordingRouterRouteHandler();

    public IReadOnlyCollection<HttpMethod> Methods { get; } = new List<HttpMethod>();

    public RoutePattern? Pattern => null;

    public decimal InboundPrecedence => 0m;

    public IRouterRouteMetadataCollection Metadata { get; }

    public bool TryMatchPath(IHttpContext context, out RouteValueDictionary values)
    {
        values = new RouteValueDictionary();
        return false;
    }

    public bool TryMatch(IHttpContext context, out RouteValueDictionary values)
    {
        values = new RouteValueDictionary();
        return false;
    }
}
