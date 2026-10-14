namespace Assimalign.Cohesion.Web.Routing.Internal;

/// <summary>
/// Default <see cref="IRouterRouteBuilder"/>: attaches metadata to one mapped route through the route's
/// <see cref="DeferredRouteMetadata"/>, which composes it when the route table is built.
/// </summary>
internal sealed class RouterRouteBuilder : IRouterRouteBuilder
{
    private readonly DeferredRouteMetadata _metadata;

    /// <summary>
    /// Initializes a builder over the route's deferred metadata.
    /// </summary>
    /// <param name="metadata">The deferred metadata the mapped route carries.</param>
    public RouterRouteBuilder(DeferredRouteMetadata metadata)
    {
        _metadata = metadata;
    }

    /// <inheritdoc />
    public IRouterRouteBuilder WithMetadata(params object[] items)
    {
        DeferredRouteMetadata.Validate(items);
        _metadata.Add(items);

        return this;
    }

    IRouterConventionBuilder IRouterConventionBuilder.WithMetadata(params object[] items) => WithMetadata(items);
}
