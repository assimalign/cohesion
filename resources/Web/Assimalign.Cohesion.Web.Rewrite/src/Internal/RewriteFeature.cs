using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Rewrite.Internal;

/// <summary>
/// Default <see cref="IWebRewriteFeature"/>, published by the first rewrite of a request for as long as the
/// rest of the pipeline runs.
/// </summary>
internal sealed class RewriteFeature : IWebRewriteFeature
{
    public RewriteFeature(HttpPath originalPath, IHttpQueryCollection originalQuery)
    {
        OriginalPath = originalPath;
        OriginalQuery = originalQuery;
    }

    /// <inheritdoc />
    public string Name => nameof(IWebRewriteFeature);

    /// <inheritdoc />
    public HttpPath OriginalPath { get; }

    /// <inheritdoc />
    public IHttpQueryCollection OriginalQuery { get; }
}
