using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Routing.Internal;

/// <summary>
/// Default <see cref="IRouterPathBaseFeature"/>, installed by a path branch for the duration of the branch.
/// </summary>
internal sealed class RouterPathBaseFeature : IRouterPathBaseFeature
{
    /// <summary>
    /// Initializes the feature with the accumulated path base and the path below it.
    /// </summary>
    /// <param name="pathBase">The prefixes the entered branches matched, outermost first.</param>
    /// <param name="path">The request path below <paramref name="pathBase"/>.</param>
    public RouterPathBaseFeature(HttpPath pathBase, HttpPath path)
    {
        PathBase = pathBase;
        Path = path;
    }

    /// <inheritdoc />
    public string Name => nameof(IRouterPathBaseFeature);

    /// <inheritdoc />
    public HttpPath PathBase { get; }

    /// <inheritdoc />
    public HttpPath Path { get; }
}
