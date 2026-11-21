using System;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// Queues the frames a connection sends and writes them to its carrier on one task, so frames reach the
/// carrier in the order they were queued and no caller waits on the carrier while it encodes.
/// </summary>
/// <remarks>
/// <para>
/// Every member but <see cref="RunAsync"/> is called under the connection's lock. A caller encodes frames
/// straight into <see cref="Buffer"/> and then calls <see cref="Schedule"/>. The loop swaps the buffer
/// callers fill for the one it writes, so encoding continues while the carrier writes.
/// </para>
/// <para>
/// A sender that produces faster than the carrier drains waits on <see cref="WaitForCapacity"/> once the
/// queued bytes pass the high-water mark. Frames the connection itself answers with, such as a
/// <c>flow</c> or a <c>close</c>, never wait.
/// </para>
/// </remarks>
[DebuggerDisplay("Pending = {PendingLength}")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class AmqpFrameWriter : IDisposable
{
    private const int minHighWater = 64 * 1024;
    private const int maxHighWater = 8 * 1024 * 1024;

    private readonly Lock _sync;
    private readonly PipeWriter _output;
    private readonly SemaphoreSlim _signal;
    private readonly AmqpWaiter _capacity;
    private readonly int _highWater;
    private AmqpBufferWriter _pending;
    private AmqpBufferWriter _writing;
    private bool _scheduled;
    private bool _completing;
    private bool _stopped;
    private long _lastWriteTicks;

    public AmqpFrameWriter(Lock sync, PipeWriter output, uint maxFrameSize)
    {
        _sync = sync;
        _output = output;
        _signal = new SemaphoreSlim(0, 1);
        _capacity = new AmqpWaiter();
        _highWater = (int)Math.Clamp(4L * maxFrameSize, minHighWater, maxHighWater);
        _pending = new AmqpBufferWriter();
        _writing = new AmqpBufferWriter();
        _lastWriteTicks = Environment.TickCount64;
    }

    /// <summary>
    /// Gets the buffer callers encode frames into, under the connection's lock.
    /// </summary>
    public AmqpBufferWriter Buffer
    {
        get
        {
            Debug.Assert(_sync.IsHeldByCurrentThread);

            return _pending;
        }
    }

    /// <summary>
    /// Gets a value indicating whether the writer accepts no more frames: it is completing or stopped.
    /// </summary>
    public bool IsClosed => _completing || _stopped;

    /// <summary>
    /// Gets the number of bytes queued and not yet handed to the carrier.
    /// </summary>
    public int PendingLength => _pending.Length;

    /// <summary>
    /// Gets the <see cref="Environment.TickCount64"/> value of the last completed write.
    /// </summary>
    public long LastWriteTicks => Volatile.Read(ref _lastWriteTicks);

    /// <summary>
    /// Wakes the loop to write the frames queued in <see cref="Buffer"/>.
    /// </summary>
    public void Schedule()
    {
        Debug.Assert(_sync.IsHeldByCurrentThread);

        if (!_scheduled && !_stopped && _pending.Length > 0)
        {
            _scheduled = true;
            _signal.Release();
        }
    }

    /// <summary>
    /// Gets a value indicating whether the queue is below the high-water mark, under the lock. A stopped
    /// writer reports capacity, so a waiting caller moves on to find the connection closed.
    /// </summary>
    public bool HasCapacity
    {
        get
        {
            Debug.Assert(_sync.IsHeldByCurrentThread);

            return _pending.Length < _highWater || _stopped;
        }
    }

    /// <summary>
    /// Returns a task that completes when the queue drops below the high-water mark, or
    /// <see langword="null"/> when it already is. The caller awaits the task outside the lock and then
    /// checks again.
    /// </summary>
    public Task? WaitForCapacity()
    {
        Debug.Assert(_sync.IsHeldByCurrentThread);

        return HasCapacity ? null : _capacity.WaitAsync();
    }

    /// <summary>
    /// Writes an empty frame when nothing was written or queued for <paramref name="interval"/>
    /// milliseconds, which keeps the peer's idle timer from expiring (AMQP 1.0 Part 2, section 2.4.5).
    /// </summary>
    public void WriteHeartbeatIfIdle(long now, long interval)
    {
        Debug.Assert(_sync.IsHeldByCurrentThread);

        if (IsClosed || _pending.Length > 0 || now - LastWriteTicks < interval)
        {
            return;
        }

        AmqpFrame.WriteEmpty(_pending);
        Schedule();
    }

    /// <summary>
    /// Asks the loop to write what is queued and then complete the carrier's output.
    /// </summary>
    public void Complete()
    {
        Debug.Assert(_sync.IsHeldByCurrentThread);

        if (_completing || _stopped)
        {
            return;
        }

        _completing = true;

        if (!_scheduled)
        {
            _scheduled = true;
            _signal.Release();
        }
    }

    /// <summary>
    /// Stops the loop without writing what is queued, and cancels a write in progress.
    /// </summary>
    public void Abort()
    {
        Debug.Assert(_sync.IsHeldByCurrentThread);

        if (_stopped)
        {
            return;
        }

        _stopped = true;
        _capacity.Signal();
        _output.CancelPendingFlush();

        if (!_scheduled)
        {
            _scheduled = true;
            _signal.Release();
        }
    }

    /// <summary>
    /// Runs the write loop until the writer completes, aborts or the carrier fails.
    /// </summary>
    /// <returns>
    /// A task that completes with <see langword="null"/> when the loop ended as asked, or with the
    /// exception that ended it; the loop never faults.
    /// </returns>
    public async Task<Exception?> RunAsync()
    {
        Exception? failure = null;

        try
        {
            while (true)
            {
                await _signal.WaitAsync().ConfigureAwait(false);

                AmqpBufferWriter batch;
                bool completing;

                lock (_sync)
                {
                    _scheduled = false;

                    if (_stopped)
                    {
                        break;
                    }

                    batch = _pending;
                    completing = _completing;

                    if (batch.Length > 0)
                    {
                        _pending = _writing;
                        _writing = batch;
                        _capacity.Signal();
                    }
                }

                if (batch.Length > 0)
                {
                    FlushResult result = await _output.WriteAsync(batch.WrittenMemory).ConfigureAwait(false);

                    batch.Reset();
                    Volatile.Write(ref _lastWriteTicks, Environment.TickCount64);

                    if (result.IsCanceled || result.IsCompleted)
                    {
                        // Canceled by Abort, or the carrier's reader is gone: nothing more can be written.
                        break;
                    }

                    lock (_sync)
                    {
                        // More frames arrived while the write ran: write them before completing.
                        if (_pending.Length > 0 && !_stopped)
                        {
                            if (!_scheduled)
                            {
                                _scheduled = true;
                                _signal.Release();
                            }

                            continue;
                        }
                    }
                }

                if (completing)
                {
                    await _output.CompleteAsync().ConfigureAwait(false);
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            // Justified catch-all: the loop runs detached, so whatever ended the carrier's output is handed
            // to the connection, which owns the failure, instead of faulting an unobserved task.
            failure = exception;
        }

        lock (_sync)
        {
            _stopped = true;
            _capacity.Signal();
        }

        return failure;
    }

    /// <summary>
    /// Returns the buffers to the pool. Called once the loop has ended.
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            _stopped = true;
            _pending.Dispose();
            _writing.Dispose();
        }

        _signal.Dispose();
    }

    private sealed class DebugView
    {
        private readonly AmqpFrameWriter _writer;

        public DebugView(AmqpFrameWriter writer)
        {
            _writer = writer;
        }

        public bool IsClosed => _writer.IsClosed;
        public int PendingLength => _writer.PendingLength;
        public long LastWriteTicks => _writer.LastWriteTicks;
    }
}
