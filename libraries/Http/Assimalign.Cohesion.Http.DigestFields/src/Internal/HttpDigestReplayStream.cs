using System.IO;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// A read-only, seekable replay of a request body that the digest verifier fully consumed to hash
/// it. Reads serve the buffered bytes; disposal also disposes the original wrapped body so the
/// interceptor honors the seam's "a wrapper owns the stream it wraps" contract.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("HttpDigestReplayStream {Length,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
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

    private sealed class DebugView
    {
        private readonly HttpDigestReplayStream _target;

        public DebugView(HttpDigestReplayStream value)
        {
            _target = value;
        }

        public bool CanRead => _target.CanRead;
        public bool CanSeek => _target.CanSeek;
        public bool CanWrite => _target.CanWrite;
        public int Capacity => _target.Capacity;
        public long Length => _target.Length;
        public long Position => _target.Position;
        public bool CanTimeout => _target.CanTimeout;
        public int ReadTimeout => _target.ReadTimeout;
        public int WriteTimeout => _target.WriteTimeout;
    }
}
