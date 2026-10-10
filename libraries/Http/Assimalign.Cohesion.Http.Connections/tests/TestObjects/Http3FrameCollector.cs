using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>
/// Accumulates what the server writes on one HTTP/3 request stream — the client end of an in-memory
/// stream (<see cref="Http3InMemoryPeer"/>), or any reader over a real QUIC stream — and parses it into
/// complete frames as they arrive, so an interactive test can wait for a specific frame and react to it.
/// Records whether the server ended its side of the stream (FIN) or reset it, in which case the
/// in-memory driver surfaces the server's reason.
/// </summary>
internal sealed class Http3FrameCollector
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    private readonly PipeReader _input;
    private readonly List<byte> _pending = new();
    private readonly List<(long FrameType, byte[] Payload)> _frames = new();

    public Http3FrameCollector(Connection stream)
        : this(stream.Input)
    {
    }

    public Http3FrameCollector(PipeReader input)
    {
        _input = input;
    }

    /// <summary>Every complete frame observed so far, in wire order.</summary>
    public IReadOnlyList<(long FrameType, byte[] Payload)> Frames => _frames;

    /// <summary>Whether the server ended its side of the stream (FIN).</summary>
    public bool IsCompleted { get; private set; }

    /// <summary>The reason the stream failed when the server reset it, or <see langword="null"/>.</summary>
    public Exception? Failure { get; private set; }

    /// <summary>The total DATA payload octets observed.</summary>
    public int DataLength => _frames.Where(frame => frame.FrameType == 0x0).Sum(frame => frame.Payload.Length);

    /// <summary>The DATA payload observed, reassembled in order.</summary>
    public byte[] DataPayload()
    {
        using MemoryStream payload = new();

        foreach ((long frameType, byte[] framePayload) in _frames)
        {
            if (frameType == 0x0)
            {
                payload.Write(framePayload, 0, framePayload.Length);
            }
        }

        return payload.ToArray();
    }

    /// <summary>
    /// Reads the server's output until <paramref name="predicate"/> holds. Fails the test when the stream
    /// ends, or nothing satisfying arrives within the timeout.
    /// </summary>
    /// <param name="predicate">The condition over this collector's state.</param>
    /// <param name="description">What the test is waiting for, used in the failure message.</param>
    /// <returns>A task that completes once the condition holds.</returns>
    public async Task ReadUntilAsync(Func<Http3FrameCollector, bool> predicate, string description)
    {
        using CancellationTokenSource timeout = new(_timeout);

        while (!predicate(this))
        {
            if (IsCompleted || Failure is not null)
            {
                throw new ShouldAssertException($"The server's side of the stream ended before {description}.");
            }

            ReadResult result;

            try
            {
                result = await _input.ReadAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new ShouldAssertException($"Timed out waiting for {description}.");
            }
            catch (Exception exception)
            {
                Failure = exception;
                continue;
            }

            foreach (ReadOnlyMemory<byte> segment in result.Buffer)
            {
                _pending.AddRange(segment.ToArray());
            }

            _input.AdvanceTo(result.Buffer.End);

            if (result.IsCompleted)
            {
                IsCompleted = true;
            }

            ParseCompleteFrames();
        }
    }

    private void ParseCompleteFrames()
    {
        int index = 0;

        while (TryReadVarint(index, out long frameType, out int typeLength)
            && TryReadVarint(index + typeLength, out long length, out int lengthLength)
            && _pending.Count - index - typeLength - lengthLength >= length)
        {
            int payloadStart = index + typeLength + lengthLength;
            _frames.Add((frameType, _pending.GetRange(payloadStart, (int)length).ToArray()));
            index = payloadStart + (int)length;
        }

        _pending.RemoveRange(0, index);
    }

    private bool TryReadVarint(int index, out long value, out int length)
    {
        value = 0;
        length = 0;

        if (index >= _pending.Count)
        {
            return false;
        }

        // RFC 9000 §16 — the two most significant bits of the first octet give the encoded length.
        length = 1 << (_pending[index] >> 6);

        if (_pending.Count - index < length)
        {
            return false;
        }

        value = _pending[index] & 0x3F;

        for (int offset = 1; offset < length; offset++)
        {
            value = (value << 8) | _pending[index + offset];
        }

        return true;
    }
}
