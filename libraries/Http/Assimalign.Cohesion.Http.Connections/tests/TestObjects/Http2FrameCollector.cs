using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>
/// Accumulates everything the server writes to a <see cref="TestConnection"/> and parses it into
/// complete HTTP/2 frames as they arrive, so an interactive test can wait for a specific frame and
/// then react to it. A frame split across reads is held until its remaining octets arrive.
/// </summary>
internal sealed class Http2FrameCollector
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    private readonly TestConnection _connection;
    private readonly List<byte> _pending = new();
    private readonly List<Http2WireFrame> _frames = new();
    private bool _outputCompleted;

    public Http2FrameCollector(TestConnection connection)
    {
        _connection = connection;
    }

    /// <summary>Every complete frame observed so far, in wire order.</summary>
    public IReadOnlyList<Http2WireFrame> Frames => _frames;

    /// <summary>
    /// Reads server output until <paramref name="predicate"/> holds over the frames observed so far.
    /// Fails the test when the output ends or nothing satisfying arrives within the timeout.
    /// </summary>
    /// <param name="predicate">The condition over every frame observed so far.</param>
    /// <param name="description">What the test is waiting for, used in the failure message.</param>
    /// <returns>The frames observed so far.</returns>
    public async Task<IReadOnlyList<Http2WireFrame>> ReadUntilAsync(Func<IReadOnlyList<Http2WireFrame>, bool> predicate, string description)
    {
        using CancellationTokenSource timeout = new(_timeout);

        while (!predicate(_frames))
        {
            if (_outputCompleted)
            {
                throw new ShouldAssertException($"The server output ended before {description}.");
            }

            byte[] chunk;
            try
            {
                chunk = await _connection.ReadOutputAsync().WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new ShouldAssertException($"Timed out waiting for {description}.");
            }

            if (chunk.Length == 0)
            {
                // A read that returns nothing means the server completed its output.
                _outputCompleted = true;
                continue;
            }

            _pending.AddRange(chunk);
            ParseCompleteFrames();
        }

        return _frames;
    }

    /// <summary>The total DATA payload octets observed on <paramref name="streamId"/>.</summary>
    public int DataLength(int streamId) => _frames.Where(frame => frame.IsData && frame.StreamId == streamId).Sum(frame => frame.Payload.Length);

    /// <summary>The DATA payload observed on <paramref name="streamId"/>, reassembled in order.</summary>
    public byte[] DataPayload(int streamId)
    {
        using MemoryStream payload = new();

        foreach (Http2WireFrame frame in _frames.Where(frame => frame.IsData && frame.StreamId == streamId))
        {
            payload.Write(frame.Payload, 0, frame.Payload.Length);
        }

        return payload.ToArray();
    }

    /// <summary>The frames observed on <paramref name="streamId"/>, in wire order.</summary>
    public IReadOnlyList<Http2WireFrame> ForStream(int streamId) => _frames.Where(frame => frame.StreamId == streamId).ToList();

    private void ParseCompleteFrames()
    {
        int index = 0;

        while (_pending.Count - index >= 9)
        {
            int length = (_pending[index] << 16) | (_pending[index + 1] << 8) | _pending[index + 2];
            if (_pending.Count - index - 9 < length)
            {
                break;
            }

            byte type = _pending[index + 3];
            byte flags = _pending[index + 4];
            int streamId = ((_pending[index + 5] & 0x7F) << 24)
                | (_pending[index + 6] << 16)
                | (_pending[index + 7] << 8)
                | _pending[index + 8];
            byte[] payload = _pending.GetRange(index + 9, length).ToArray();

            _frames.Add(new Http2WireFrame(type, flags, streamId, payload));
            index += 9 + length;
        }

        _pending.RemoveRange(0, index);
    }
}
