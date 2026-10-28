using System.Net.Http;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

/// <summary>
/// An HTTP client over a handler it does not own, as a client factory hands out, that records its
/// disposal.
/// </summary>
internal sealed class TrackingHttpClient : HttpClient
{
    public TrackingHttpClient(HttpMessageHandler handler)
        : base(handler, disposeHandler: false)
    {
    }

    public bool IsDisposed { get; private set; }

    protected override void Dispose(bool disposing)
    {
        IsDisposed = true;
        base.Dispose(disposing);
    }
}
