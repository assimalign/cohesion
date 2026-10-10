using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

/// <summary>
/// The default server's <see cref="IWebClientFaultFeature"/>: a read-through view of the exchange
/// control's client-fault report, installed by <see cref="WebClientFaultInterceptor"/>.
/// </summary>
/// <remarks>
/// It reads the control on every access rather than copying the status, because the transport latches
/// the status while the pipeline runs, when the application's body read fails.
/// </remarks>
internal sealed class WebClientFaultFeature : IWebClientFaultFeature
{
    private readonly IHttpExchangeControl _control;

    /// <summary>
    /// Initializes the feature over the exchange's control.
    /// </summary>
    /// <param name="control">The transport's control for the exchange.</param>
    public WebClientFaultFeature(IHttpExchangeControl control)
    {
        _control = control;
    }

    /// <inheritdoc />
    public string Name => nameof(IWebClientFaultFeature);

    /// <inheritdoc />
    public HttpStatusCode? StatusCode => _control.ClientFaultStatusCode;
}
