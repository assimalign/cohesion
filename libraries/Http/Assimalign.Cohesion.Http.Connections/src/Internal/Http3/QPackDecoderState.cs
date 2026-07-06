using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Connections.Internal.Http3.Frames;
using Assimalign.Cohesion.Http.Connections.Internal.Http3.QPack;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http3;

/// <summary>
/// The decoder-side QPACK state shared across all request streams of one HTTP/3
/// connection: the dynamic table, the insert-count coordination that lets a
/// request field section block until its referenced insertions arrive
/// (RFC 9204 §2.1.2), and the blocked-stream budget that caps how many streams
/// may block at once.
/// </summary>
/// <remarks>
/// <para>
/// Two loops touch this state concurrently: the background encoder-stream drain
/// (which applies insertions via <see cref="ApplyEncoderInstructions"/>) and the
/// accept loop (which decodes request field sections via
/// <see cref="DecodeRequestAsync"/>). All dynamic-table access is serialized by a
/// single lock; the blocking wait releases the lock while awaiting so the drain
/// can make progress.
/// </para>
/// <para>
/// A blocked stream that would exceed the advertised
/// <c>QPACK_BLOCKED_STREAMS</c>, or a field section whose Required Insert Count
/// can never be satisfied, is a connection error
/// (<c>QPACK_DECOMPRESSION_FAILED</c>, RFC 9204 §2.2).
/// </para>
/// </remarks>
internal sealed class QPackDecoderState
{
    private readonly QPackDynamicTable _table;
    private readonly long _maxBlockedStreams;
    private readonly Lock _gate = new();
    private long _blockedStreams;
    private TaskCompletionSource _insertSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Initializes decoder state for the supplied QPACK options.
    /// </summary>
    /// <param name="options">The advertised capacity and blocked-stream limit.</param>
    public QPackDecoderState(Http3QPackOptions options)
    {
        _table = new QPackDynamicTable(options.MaxTableCapacity);
        _maxBlockedStreams = options.MaxBlockedStreams;
    }

    /// <summary>
    /// Applies the QPACK encoder-stream instructions buffered in
    /// <paramref name="buffer"/>, stopping at the first incomplete instruction.
    /// </summary>
    /// <param name="buffer">The buffered, not-yet-consumed encoder-stream octets.</param>
    /// <param name="insertionsApplied">The number of entries inserted (for Insert Count Increment).</param>
    /// <returns>The number of octets consumed; the caller keeps the remainder buffered.</returns>
    /// <exception cref="QPackException">Thrown on a malformed instruction or table violation.</exception>
    public int ApplyEncoderInstructions(ReadOnlySpan<byte> buffer, out int insertionsApplied)
    {
        int totalConsumed = 0;
        int inserts = 0;

        lock (_gate)
        {
            while (QPackEncoderInstructionParser.TryApplyNext(buffer[totalConsumed..], _table, out int consumed, out bool inserted))
            {
                totalConsumed += consumed;

                if (inserted)
                {
                    inserts++;
                }
            }

            if (inserts > 0)
            {
                // Release every stream waiting on a higher insert count, then arm
                // a fresh signal for the next batch. RunContinuationsAsynchronously
                // keeps waiter continuations off this lock.
                TaskCompletionSource previous = _insertSignal;
                _insertSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                previous.TrySetResult();
            }
        }

        insertionsApplied = inserts;
        return totalConsumed;
    }

    /// <summary>
    /// Decodes a request field section, blocking (within the blocked-stream
    /// budget) until the dynamic table holds enough insertions to satisfy the
    /// section's Required Insert Count.
    /// </summary>
    /// <param name="headerBlock">The encoded QPACK field section.</param>
    /// <param name="cancellationToken">A token cancelled on connection teardown.</param>
    /// <returns>
    /// The decoded field lines and whether the section referenced the dynamic
    /// table (a Section Acknowledgment is owed for referencing sections).
    /// </returns>
    /// <exception cref="QPackException">Thrown on an unsatisfiable section or a blocked-stream overflow.</exception>
    public async Task<QPackDecodeResult> DecodeRequestAsync(byte[] headerBlock, CancellationToken cancellationToken)
    {
        QPackFieldSectionPrefix prefix;

        lock (_gate)
        {
            prefix = QPackFieldSectionPrefix.Parse(headerBlock, _table.MaxCapacity, _table.InsertCount);
        }

        if (prefix.RequiredInsertCount > 0)
        {
            await WaitForInsertCountAsync(prefix.RequiredInsertCount, cancellationToken).ConfigureAwait(false);
        }

        lock (_gate)
        {
            List<(string Name, string Value)> fields = QPackFieldSectionDecoder.DecodeBody(headerBlock, _table, prefix);
            return new QPackDecodeResult(fields, prefix.RequiredInsertCount > 0);
        }
    }

    private async Task WaitForInsertCountAsync(long target, CancellationToken cancellationToken)
    {
        bool counted = false;

        try
        {
            while (true)
            {
                Task signal;

                lock (_gate)
                {
                    if (_table.InsertCount >= target)
                    {
                        return;
                    }

                    if (!counted)
                    {
                        if (_blockedStreams >= _maxBlockedStreams)
                        {
                            // The peer blocked more streams than it was permitted
                            // to (RFC 9204 §2.1.2) — a connection error.
                            throw new QPackException(
                                Http3ErrorCode.QPackDecompressionFailed,
                                $"A QPACK field section would block more than the permitted {_maxBlockedStreams} stream(s).");
                        }

                        _blockedStreams++;
                        counted = true;
                    }

                    signal = _insertSignal.Task;
                }

                await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (counted)
            {
                lock (_gate)
                {
                    _blockedStreams--;
                }
            }
        }
    }
}
