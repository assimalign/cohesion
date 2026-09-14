using System.Net.Http;

namespace Assimalign.Cohesion.Rezolvr.Client.Tests;

internal sealed class RecordingHttpMessageInvoker() : HttpMessageInvoker(new SocketsHttpHandler())
{
    internal bool IsDisposed { get; private set; }

    protected override void Dispose(bool disposing)
    {
        IsDisposed = true;
        base.Dispose(disposing);
    }
}
