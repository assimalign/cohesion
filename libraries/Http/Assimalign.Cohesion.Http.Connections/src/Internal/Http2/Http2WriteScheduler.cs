using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http2;

/// <summary>
/// The connection-level write gate that serializes every outbound frame write
/// and, when multiple writers contend, grants the gate in RFC 9218 §10 priority
/// order rather than first-come-first-served.
/// </summary>
/// <remarks>
/// <para>
/// This type replaces the plain FIFO write semaphore. It preserves the
/// non-interleaving invariant (RFC 9113 §4.1 — frames from concurrent senders
/// MUST NOT interleave: exactly one holder at a time) while adding the RFC 9218
/// scheduling policy: connection-control frames (SETTINGS/PING ACK,
/// WINDOW_UPDATE, RST_STREAM, GOAWAY) always go first; response writes are then
/// ordered by ascending <see cref="HttpPriority.Urgency"/>; at a given urgency a
/// non-incremental response is written to completion before incremental ones;
/// and same-urgency incremental streams are served round-robin by stream id.
/// </para>
/// <para>
/// Because a response is buffered and written as one contiguous
/// HEADERS+DATA unit while the gate is held (the streaming write path is a
/// separate concern), the scheduler orders <em>which stream's queued response
/// proceeds next</em> under write contention — the point at which HTTP/2's
/// single connection write path is actually contended. The pure ordering policy
/// lives in <see cref="SelectNextWaiterIndex"/> so it can be unit-tested in
/// isolation.
/// </para>
/// </remarks>
internal sealed class Http2WriteScheduler : IDisposable
{
    /// <summary>
    /// The sentinel urgency for connection-control frames. Below the RFC 9218
    /// minimum urgency of 0 so control writes always outrank any response DATA
    /// write, regardless of how urgent a response claims to be.
    /// </summary>
    public const int ControlUrgency = -1;

    private readonly object _sync = new();
    private readonly List<Waiter> _waiters = new();
    private bool _held;
    private long _sequence;
    private int _lastServedIncrementalStreamId = -1;
    private bool _disposed;

    /// <summary>
    /// Acquires the connection write gate for a writer with the supplied priority,
    /// completing when the gate is granted. The caller MUST invoke
    /// <see cref="Release"/> exactly once when its write completes.
    /// </summary>
    /// <param name="streamId">The stream the write belongs to (0 for connection-control frames).</param>
    /// <param name="urgency">The write's urgency; <see cref="ControlUrgency"/> for control frames.</param>
    /// <param name="incremental">Whether the write's stream is incremental (RFC 9218 §4.2).</param>
    /// <param name="cancellationToken">A token that cancels a still-queued acquisition.</param>
    /// <returns>A task that completes when the gate has been granted to this writer.</returns>
    public Task AcquireAsync(int streamId, int urgency, bool incremental, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return Task.FromException(new ObjectDisposedException(nameof(Http2WriteScheduler)));
            }

            Waiter waiter = new(streamId, urgency, incremental, _sequence++);
            _waiters.Add(waiter);
            TryGrantLocked();

            // Monitor is reentrant, so a synchronously-firing cancellation
            // registration re-entering CancelWaiter under this same lock is safe.
            if (!waiter.Completion.IsCompleted)
            {
                waiter.AttachCancellation(cancellationToken, this);
            }

            return waiter.Completion;
        }
    }

    /// <summary>
    /// Releases the connection write gate, allowing the next-highest-priority
    /// queued writer (if any) to proceed.
    /// </summary>
    public void Release()
    {
        lock (_sync)
        {
            _held = false;
            TryGrantLocked();
        }
    }

    private void TryGrantLocked()
    {
        if (_held || _waiters.Count == 0)
        {
            return;
        }

        int index = SelectNextWaiterIndex(_waiters, _lastServedIncrementalStreamId);
        Waiter next = _waiters[index];
        _waiters.RemoveAt(index);
        _held = true;

        // Advance the round-robin cursor only for incremental response writes;
        // control writes and non-incremental writes do not rotate it.
        if (next.Incremental && next.Urgency >= HttpPriority.MinUrgency)
        {
            _lastServedIncrementalStreamId = next.StreamId;
        }

        next.Grant();
    }

    private void CancelWaiter(Waiter waiter)
    {
        lock (_sync)
        {
            if (_waiters.Remove(waiter))
            {
                waiter.Cancel();
            }
        }
    }

    /// <summary>
    /// The pure ordering policy: selects the index of the waiter to grant next.
    /// Ordering is (1) lowest urgency — <see cref="ControlUrgency"/> is always
    /// lowest; (2) at that urgency, non-incremental before incremental, breaking
    /// ties by arrival order; (3) among same-urgency incremental waiters,
    /// round-robin by stream id — the smallest id strictly greater than the last
    /// served, wrapping to the smallest id overall.
    /// </summary>
    /// <param name="waiters">The queued waiters (non-empty).</param>
    /// <param name="lastServedIncrementalStreamId">The stream id last granted from the incremental rotation, or -1.</param>
    /// <returns>The index into <paramref name="waiters"/> of the waiter to grant.</returns>
    internal static int SelectNextWaiterIndex(IReadOnlyList<Waiter> waiters, int lastServedIncrementalStreamId)
    {
        int minUrgency = int.MaxValue;
        for (int i = 0; i < waiters.Count; i++)
        {
            if (waiters[i].Urgency < minUrgency)
            {
                minUrgency = waiters[i].Urgency;
            }
        }

        // Non-incremental writes at the winning urgency take precedence, in
        // arrival (FIFO) order.
        int bestNonIncremental = -1;
        for (int i = 0; i < waiters.Count; i++)
        {
            Waiter waiter = waiters[i];
            if (waiter.Urgency != minUrgency || waiter.Incremental)
            {
                continue;
            }

            if (bestNonIncremental < 0 || waiter.Sequence < waiters[bestNonIncremental].Sequence)
            {
                bestNonIncremental = i;
            }
        }

        if (bestNonIncremental >= 0)
        {
            return bestNonIncremental;
        }

        // All remaining candidates at the winning urgency are incremental —
        // rotate by stream id so no single incremental stream monopolizes the
        // gate (RFC 9218 §10).
        int smallest = -1;
        int nextAfterCursor = -1;
        for (int i = 0; i < waiters.Count; i++)
        {
            Waiter waiter = waiters[i];
            if (waiter.Urgency != minUrgency || !waiter.Incremental)
            {
                continue;
            }

            if (smallest < 0 || waiter.StreamId < waiters[smallest].StreamId)
            {
                smallest = i;
            }

            if (waiter.StreamId > lastServedIncrementalStreamId
                && (nextAfterCursor < 0 || waiter.StreamId < waiters[nextAfterCursor].StreamId))
            {
                nextAfterCursor = i;
            }
        }

        return nextAfterCursor >= 0 ? nextAfterCursor : smallest;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (Waiter waiter in _waiters)
            {
                waiter.Cancel();
            }

            _waiters.Clear();
        }
    }

    /// <summary>
    /// A single queued acquisition. Exposed as <c>internal</c> so the ordering
    /// policy in <see cref="SelectNextWaiterIndex"/> can be exercised directly by
    /// the co-located tests.
    /// </summary>
    internal sealed class Waiter
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _registration;

        public Waiter(int streamId, int urgency, bool incremental, long sequence)
        {
            StreamId = streamId;
            Urgency = urgency;
            Incremental = incremental;
            Sequence = sequence;
        }

        /// <summary>The stream the write belongs to (0 for connection-control frames).</summary>
        public int StreamId { get; }

        /// <summary>The write's urgency; <see cref="ControlUrgency"/> for control frames.</summary>
        public int Urgency { get; }

        /// <summary>Whether the write's stream is incremental.</summary>
        public bool Incremental { get; }

        /// <summary>The monotonic arrival order, used to break ties in FIFO fashion.</summary>
        public long Sequence { get; }

        /// <summary>The task that completes when this waiter is granted the gate.</summary>
        public Task Completion => _completion.Task;

        public void AttachCancellation(CancellationToken cancellationToken, Http2WriteScheduler scheduler)
        {
            if (!cancellationToken.CanBeCanceled)
            {
                return;
            }

            _registration = cancellationToken.Register(
                static state =>
                {
                    (Http2WriteScheduler scheduler, Waiter waiter) = ((Http2WriteScheduler, Waiter))state!;
                    scheduler.CancelWaiter(waiter);
                },
                (scheduler, this));
        }

        public void Grant()
        {
            _registration.Dispose();
            _completion.TrySetResult();
        }

        public void Cancel()
        {
            _registration.Dispose();
            _completion.TrySetCanceled();
        }
    }
}
