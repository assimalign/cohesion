using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// Default <see cref="IHttpInterimResponseFeature"/> implementation. A thin typed API over the
/// transport's exchange control (<see cref="HttpExchangeInterceptorResponseContext.Control"/>): the
/// control owns the per-version wire emission and the status-code / ordering rules, while this
/// feature owns the application-facing surface and its resolution from the feature collection.
/// </summary>
internal sealed class HttpInterimResponseFeature : IHttpInterimResponseFeature
{
    /// <summary>The name under which the interim-response feature is registered.</summary>
    public const string FeatureName = "Assimalign.Cohesion.Http.InterimResponse";

    private readonly IHttpExchangeControl _control;

    public HttpInterimResponseFeature(IHttpExchangeControl control)
    {
        _control = control;
    }

    /// <inheritdoc />
    public string Name => FeatureName;

    /// <inheritdoc />
    public bool IsInterimResponseSupported => _control.CanWriteInterimResponse;

    /// <inheritdoc />
    public ValueTask SendInterimResponseAsync(
        HttpStatusCode statusCode,
        IHttpHeaderCollection? headers = null,
        CancellationToken cancellationToken = default)
        => _control.WriteInterimResponseAsync(statusCode, headers, cancellationToken);
}
