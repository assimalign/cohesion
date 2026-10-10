using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.OpenApi.Internal;

/// <summary>
/// Carries the read-only <see cref="OpenApiOptions"/> from <c>AddOpenApi</c> (builder time) to
/// <c>MapOpenApi</c> and <c>GetOpenApiDescriptionProvider</c>. The application's registered features are
/// the only channel between builder-time and pipeline-time verbs, so the options travel as a feature, the
/// way Web.Authorization carries its policies.
/// </summary>
internal sealed class OpenApiDocumentFeature : IHttpFeature
{
    public OpenApiDocumentFeature(OpenApiOptions options)
    {
        Options = options;
    }

    /// <inheritdoc />
    public string Name => nameof(OpenApiDocumentFeature);

    /// <summary>
    /// Gets the read-only document options.
    /// </summary>
    public OpenApiOptions Options { get; }
}
