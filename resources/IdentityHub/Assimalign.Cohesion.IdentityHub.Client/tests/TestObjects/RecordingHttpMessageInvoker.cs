using System.Net.Http;

namespace Assimalign.Cohesion.IdentityHub.Client.Tests;

internal sealed class RecordingHttpMessageInvoker : HttpMessageInvoker
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingHttpMessageInvoker"/> class.
    /// </summary>
    public RecordingHttpMessageInvoker()
        : base(new SocketsHttpHandler())
    {
    }

    internal bool IsDisposed { get; private set; }

    protected override void Dispose(bool disposing)
    {
        IsDisposed = true;
        base.Dispose(disposing);
    }
}
