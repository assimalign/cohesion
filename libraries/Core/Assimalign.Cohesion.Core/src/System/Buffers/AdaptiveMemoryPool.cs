using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace System.Buffers;

/// <summary>
/// Provides a pinned byte <see cref="MemoryPool{T}"/> that adapts idle retention using a policy.
/// </summary>
/// <remarks>
/// This pool uses fixed-size pinned blocks and is intended for throughput-sensitive buffer pipelines that benefit from reuse.
/// </remarks>
public sealed class AdaptiveMemoryPool : MemoryPool<byte>
{
    private const int AnySize = -1;
    private readonly ConcurrentQueue<MemoryBlockOwner> _blocks;
    private readonly Lock _disposeSync;
    private readonly IAdaptiveMemoryPoolPolicy _policy;
    private readonly TimeProvider _timeProvider;
    private readonly ITimer? _timer;
    private readonly int _blockSize;

    private bool _isDisposed;
    private int _allocatedBlockCount;
    private int _inUseBlockCount;
    private int _peakInUseBlockCount;
    private int _retainedBlockCount;
    private long _lastRentUtcTicks;

    /// <summary>
    /// Gets the default block size used by the pool.
    /// </summary>
    public static int DefaultBlockSize => 4096;

    /// <summary>
    /// Creates a new adaptive memory pool with default options.
    /// </summary>
    public AdaptiveMemoryPool()
        : this(new AdaptiveMemoryPoolOptions())
    {
    }

    /// <summary>
    /// Creates a new adaptive memory pool.
    /// </summary>
    /// <param name="options">The options used to configure the memory pool.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    public AdaptiveMemoryPool(AdaptiveMemoryPoolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Policy);
        ArgumentNullException.ThrowIfNull(options.TimeProvider);

        _blocks = new ConcurrentQueue<MemoryBlockOwner>();
        _disposeSync = new Lock();
        _policy = options.Policy;
        _timeProvider = options.TimeProvider;
        _blockSize = options.BlockSize;
        _lastRentUtcTicks = _timeProvider.GetUtcNow().UtcTicks;

        if (options.TrimInterval > TimeSpan.Zero)
        {
            _timer = _timeProvider.CreateTimer(
                static state => ((AdaptiveMemoryPool)state!).Trim(),
                this,
                options.TrimInterval,
                options.TrimInterval);
        }
    }

    /// <summary>
    /// Gets the fixed block size used by the pool.
    /// </summary>
    public int BlockSize => _blockSize;

    /// <inheritdoc />
    public override int MaxBufferSize => _blockSize;

    /// <inheritdoc />
    public override IMemoryOwner<byte> Rent(int size = AnySize)
    {
        if (size > _blockSize)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        ThrowIfDisposed();
        RecordRent();

        if (_blocks.TryDequeue(out MemoryBlockOwner? block))
        {
            Interlocked.Decrement(ref _retainedBlockCount);
            block.MarkRented();
            IncrementInUseBlockCount();
            return block;
        }

        Interlocked.Increment(ref _allocatedBlockCount);
        IncrementInUseBlockCount();

        return new MemoryBlockOwner(this, _blockSize);
    }

    /// <summary>
    /// Returns a point-in-time snapshot of the current memory pool state.
    /// </summary>
    /// <returns>A snapshot of the current memory pool state.</returns>
    public AdaptiveMemoryPoolSnapshot GetSnapshot()
    {
        int allocatedBlockCount = Volatile.Read(ref _allocatedBlockCount);
        int inUseBlockCount = Volatile.Read(ref _inUseBlockCount);
        int retainedBlockCount = Volatile.Read(ref _retainedBlockCount);
        int peakInUseBlockCount = Volatile.Read(ref _peakInUseBlockCount);
        long lastRentUtcTicks = Volatile.Read(ref _lastRentUtcTicks);
        long currentUtcTicks = _timeProvider.GetUtcNow().UtcTicks;

        return new AdaptiveMemoryPoolSnapshot(
            _blockSize,
            allocatedBlockCount,
            inUseBlockCount,
            retainedBlockCount,
            peakInUseBlockCount,
            currentUtcTicks >= lastRentUtcTicks
                ? new TimeSpan(currentUtcTicks - lastRentUtcTicks)
                : TimeSpan.Zero);
    }

    /// <summary>
    /// Trims retained idle blocks according to the configured policy.
    /// </summary>
    public void Trim()
    {
        if (_isDisposed)
        {
            return;
        }

        TrimRetainedBlocks();
    }

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed)
        {
            return;
        }

        lock (_disposeSync)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _timer?.Dispose();

            if (disposing)
            {
                int droppedBlockCount = 0;

                while (_blocks.TryDequeue(out _))
                {
                    droppedBlockCount++;
                }

                Interlocked.Add(ref _retainedBlockCount, -droppedBlockCount);
                Interlocked.Add(ref _allocatedBlockCount, -droppedBlockCount);
            }
        }
    }

    private void RecordRent()
    {
        Interlocked.Exchange(ref _lastRentUtcTicks, _timeProvider.GetUtcNow().UtcTicks);
    }

    private void IncrementInUseBlockCount()
    {
        int inUseBlockCount = Interlocked.Increment(ref _inUseBlockCount);
        int observedPeak = Volatile.Read(ref _peakInUseBlockCount);

        while (inUseBlockCount > observedPeak)
        {
            int previousPeak = Interlocked.CompareExchange(ref _peakInUseBlockCount, inUseBlockCount, observedPeak);

            if (previousPeak == observedPeak)
            {
                break;
            }

            observedPeak = previousPeak;
        }
    }

    private void TrimRetainedBlocks()
    {
        while (true)
        {
            AdaptiveMemoryPoolSnapshot snapshot = GetSnapshot();
            int retentionLimit = _policy.GetRetentionLimit(snapshot);

            if (snapshot.RetainedBlockCount <= retentionLimit)
            {
                break;
            }

            if (!_blocks.TryDequeue(out _))
            {
                break;
            }

            Interlocked.Decrement(ref _retainedBlockCount);
            Interlocked.Decrement(ref _allocatedBlockCount);
        }
    }

    private void Return(MemoryBlockOwner block)
    {
        Interlocked.Decrement(ref _inUseBlockCount);

        if (_isDisposed)
        {
            Interlocked.Decrement(ref _allocatedBlockCount);
            return;
        }

        TrimRetainedBlocks();

        int retentionLimit = _policy.GetRetentionLimit(GetSnapshot());
        int retainedBlockCount = Interlocked.Increment(ref _retainedBlockCount);

        if (retainedBlockCount <= retentionLimit)
        {
            _blocks.Enqueue(block);
            return;
        }

        Interlocked.Decrement(ref _retainedBlockCount);
        Interlocked.Decrement(ref _allocatedBlockCount);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

    private sealed class MemoryBlockOwner : IMemoryOwner<byte>
    {
        private readonly AdaptiveMemoryPool _pool;
        private int _isDisposed;

        public MemoryBlockOwner(AdaptiveMemoryPool pool, int length)
        {
            _pool = pool;

            byte[] pinnedArray = GC.AllocateUninitializedArray<byte>(length, pinned: true);
            Memory = MemoryMarshal.CreateFromPinnedArray(pinnedArray, 0, pinnedArray.Length);
        }

        public Memory<byte> Memory { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
            {
                return;
            }

            _pool.Return(this);
        }

        public void MarkRented()
        {
            Volatile.Write(ref _isDisposed, 0);
        }
    }
}
