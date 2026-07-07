using System.IO;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// A read-only, seekable replay of a request body that the digest verifier fully consumed to hash
/// it. Reads serve the buffered bytes; disposal also disposes the original wrapped body so the
/// interceptor honors the seam's "a wrapper owns the stream it wraps" contract.
/// </summary>
internal sealed class HttpDigestReplayStream : MemoryStream
{
    private readonly Stream _original;

    public HttpDigestReplayStream(byte[] content, Stream original)
        : base(content, writable: false)
    {
        _original = original;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _original.Dispose();
        }
        base.Dispose(disposing);
    }
}
