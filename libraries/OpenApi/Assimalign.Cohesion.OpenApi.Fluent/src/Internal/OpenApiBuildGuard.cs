namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>
/// Guards version-gated fluent operations, failing fast at authoring time when a builder targeting one
/// OpenAPI line is asked to set a field only valid on another. This surfaces version mismatches where
/// they are authored rather than deferring them to a later validation pass.
/// </summary>
internal static class OpenApiBuildGuard
{
    internal static void Require(OpenApiSpecVersion version, OpenApiFeature feature, string what)
    {
        if (!OpenApiVersionCapabilities.Supports(feature, version))
        {
            throw new OpenApiException(
                $"{what} is not supported when targeting OpenAPI {OpenApiVersionCapabilities.GetVersionString(version)}.");
        }
    }
}
