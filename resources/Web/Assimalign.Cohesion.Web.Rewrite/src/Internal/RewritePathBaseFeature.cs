using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Rewrite.Internal;

/// <summary>
/// The <see cref="IWebPathBaseFeature"/> a rewrite inside a <c>Map(path)</c> branch publishes: the branch's
/// path base, unchanged, over the rewritten path below it.
/// </summary>
/// <remarks>
/// It takes the slot of the branch's own feature (the feature collection is keyed by
/// <see cref="IHttpFeature.Name"/>, and both use the contract's name), so a reader resolves exactly one view.
/// The rewrite middleware puts the branch's feature back when the rest of the pipeline returns.
/// </remarks>
internal sealed class RewritePathBaseFeature : IWebPathBaseFeature
{
    public RewritePathBaseFeature(HttpPath pathBase, HttpPath path)
    {
        PathBase = pathBase;
        Path = path;
    }

    /// <inheritdoc />
    public string Name => nameof(IWebPathBaseFeature);

    /// <inheritdoc />
    public HttpPath PathBase { get; }

    /// <inheritdoc />
    public HttpPath Path { get; }
}
