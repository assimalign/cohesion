namespace Assimalign.Cohesion.Web.Compression.Internal;

/// <summary>
/// Default <see cref="IResponseCompressionFeature"/>: a mutable per-exchange flag the middleware
/// installs so a handler can opt its response out of compression before the body starts.
/// </summary>
internal sealed class ResponseCompressionFeature : IResponseCompressionFeature
{
    /// <summary>The name under which the response-compression feature is registered.</summary>
    public const string FeatureName = "Assimalign.Cohesion.Web.ResponseCompression";

    /// <inheritdoc />
    public string Name => FeatureName;

    /// <inheritdoc />
    public bool IsEnabled { get; private set; } = true;

    /// <inheritdoc />
    public void Disable() => IsEnabled = false;
}
