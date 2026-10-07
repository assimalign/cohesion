using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>
/// A <see cref="Connection"/> decorator that follows the HTTP/2 frames the server writes and reports
/// each frame as soon as the write carrying its 9-octet header has reached the transport — whether or
/// not the frame's payload arrived in the same write. A test uses it to interfere at a precise point
/// inside the server's frame writes, for example to cancel the token a write is using right after a
/// frame's header went out.
/// </summary>
/// <remarks>
/// <para>
/// The server writes its frames through <see cref="PipeWriter.WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"/>
/// (the stream adapter over <see cref="Output"/>), which is what the tap follows; writes through
/// <see cref="PipeWriter.GetSpan"/> / <see cref="PipeWriter.Advance"/> are forwarded untracked. A
/// write the inner writer refuses is not counted, so the tap's view stays in step with what reached
/// the transport.
/// </para>
/// <para>
/// A write whose token is cancelled while it runs — by <see cref="OnFrameStarted"/> — commits its
/// octets and then throws <see cref="OperationCanceledException"/>. That is what a transport under
/// backpressure does when the token cancels the write's flush wait: the octets are already handed to
/// the transport, and only the wait is cut short.
/// </para>
/// </remarks>
internal sealed class Http2OutputTapConnection : Connection
{
    private readonly Connection _inner;
    private readonly TapPipeWriter _output;

    public Http2OutputTapConnection(Connection inner)
    {
        _inner = inner;
        _output = new TapPipeWriter(inner.Output, this);
    }

    /// <summary>
    /// Invoked after each write that carried the header of a frame, once per frame, on the server's
    /// writing thread.
    /// </summary>
    public Action<FrameHeader>? OnFrameStarted { get; set; }

    public override ConnectionId Id => _inner.Id;

    public override EndPoint? LocalEndPoint => _inner.LocalEndPoint;

    public override EndPoint? RemoteEndPoint => _inner.RemoteEndPoint;

    public override PipeReader Input => _inner.Input;

    public override PipeWriter Output => _output;

    public override ConnectionDirection Direction => _inner.Direction;

    public override ConnectionCapabilities Capabilities => _inner.Capabilities;

    public override ConnectionState State => _inner.State;

    public override CancellationToken ConnectionClosed => _inner.ConnectionClosed;

    public override void Abort(Exception? reason = null) => _inner.Abort(reason);

    public override ValueTask DisposeAsync() => _inner.DisposeAsync();

    /// <summary>The 9-octet header of one frame the server wrote (RFC 9113 §4.1).</summary>
    /// <param name="Type">The frame type octet.</param>
    /// <param name="Flags">The frame flags octet.</param>
    /// <param name="StreamId">The 31-bit stream identifier.</param>
    /// <param name="Length">The declared payload length.</param>
    public readonly record struct FrameHeader(byte Type, byte Flags, int StreamId, int Length);

    private sealed class TapPipeWriter : PipeWriter
    {
        private readonly PipeWriter _inner;
        private readonly Http2OutputTapConnection _owner;
        private readonly byte[] _header = new byte[9];
        private int _headerFilled;
        private long _payloadRemaining;

        public TapPipeWriter(PipeWriter inner, Http2OutputTapConnection owner)
        {
            _inner = inner;
            _owner = owner;
        }

        public override async ValueTask<FlushResult> WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
        {
            FlushResult result = await _inner.WriteAsync(source, cancellationToken).ConfigureAwait(false);
            List<FrameHeader>? started = Track(source.Span);

            if (started is not null)
            {
                foreach (FrameHeader header in started)
                {
                    _owner.OnFrameStarted?.Invoke(header);
                }
            }

            // A token cancelled while the write ran cuts its (modeled) flush wait short.
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }

        public override void Advance(int bytes) => _inner.Advance(bytes);

        public override Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);

        public override Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);

        public override void CancelPendingFlush() => _inner.CancelPendingFlush();

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => _inner.FlushAsync(cancellationToken);

        public override void Complete(Exception? exception = null) => _inner.Complete(exception);

        public override ValueTask CompleteAsync(Exception? exception = null) => _inner.CompleteAsync(exception);

        private List<FrameHeader>? Track(ReadOnlySpan<byte> data)
        {
            List<FrameHeader>? started = null;
            int index = 0;

            while (index < data.Length)
            {
                if (_payloadRemaining > 0)
                {
                    int skip = (int)Math.Min(_payloadRemaining, data.Length - index);
                    _payloadRemaining -= skip;
                    index += skip;
                    continue;
                }

                int copy = Math.Min(_header.Length - _headerFilled, data.Length - index);
                data.Slice(index, copy).CopyTo(_header.AsSpan(_headerFilled));
                _headerFilled += copy;
                index += copy;

                if (_headerFilled == _header.Length)
                {
                    int length = (_header[0] << 16) | (_header[1] << 8) | _header[2];
                    int streamId = (int)(BinaryPrimitives.ReadUInt32BigEndian(_header.AsSpan(5)) & 0x7FFFFFFF);
                    (started ??= new List<FrameHeader>()).Add(new FrameHeader(_header[3], _header[4], streamId, length));
                    _payloadRemaining = length;
                    _headerFilled = 0;
                }
            }

            return started;
        }
    }
}
