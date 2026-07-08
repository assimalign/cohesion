using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// Default <see cref="IHttpInterimResponseFeature"/> implementation. A thin typed API over the
/// transport's interim-response capability
/// (<see cref="HttpResponseInterceptorContext.InterimResponseWriter"/>): the capability owns the
/// per-version wire emission and the status-code / ordering rules, while this feature owns the
/// application-facing surface and its resolution from the feature collection.
/// </summary>
internal sealed class HttpInterimResponseFeature : IHttpInterimResponseFeature
{
    /// <summary>The name under which the interim-response feature is registered.</summary>
    public const string FeatureName = "Assimalign.Cohesion.Http.InterimResponse";

    private readonly IHttpInterimResponseWriter _writer;

    public HttpInterimResponseFeature(IHttpInterimResponseWriter writer)
    {
        _writer = writer;
    }

    /// <inheritdoc />
    public string Name => FeatureName;

    /// <inheritdoc />
    public bool IsInterimResponseSupported => _writer.CanWriteInterimResponse;

    /// <inheritdoc />
    public ValueTask SendInterimResponseAsync(
        HttpStatusCode statusCode,
        IHttpHeaderCollection? headers = null,
        CancellationToken cancellationToken = default)
        => _writer.WriteInterimResponseAsync(statusCode, headers, cancellationToken);
}
