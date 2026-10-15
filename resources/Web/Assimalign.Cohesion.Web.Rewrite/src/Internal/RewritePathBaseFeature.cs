using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Rewrite.Internal;

/// <summary>
/// The <see cref="IRouterPathBaseFeature"/> a rewrite inside a <c>Map(path)</c> branch publishes: the branch's
/// path base, unchanged, over the rewritten path below it.
/// </summary>
/// <remarks>
/// It takes the slot of the branch's own feature (the feature collection is keyed by
/// <see cref="IHttpFeature.Name"/>, and both use the contract's name), so a reader resolves exactly one view.
/// The rewrite middleware puts the branch's feature back when the rest of the pipeline returns.
/// </remarks>
internal sealed class RewritePathBaseFeature : IRouterPathBaseFeature
{
    public RewritePathBaseFeature(HttpPath pathBase, HttpPath path)
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
