using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Transports.Internal;

internal class SocketPipeSenderPool : IDisposable
{
    private const int MaxQueueSize = 1024; // REVIEW: Is this good enough?

    private readonly ConcurrentQueue<SocketPipeSender> _queue = new();
    private int _count;
    private readonly PipeScheduler _scheduler;
    private bool _disposed;

    public SocketPipeSenderPool(PipeScheduler scheduler)
    {
        _scheduler = scheduler;
    }

    public SocketPipeSender Rent()
    {
        if (_queue.TryDequeue(out var sender))
        {
            Interlocked.Decrement(ref _count);
            return sender;
        }
        return new SocketPipeSender(_scheduler);
    }
    public IEnumerable<SocketPipeSender> Rent(int count)
    {
        for (int i = 0 ; i < count; i++)
        {
            if (_queue.TryDequeue(out var sender))
            {
                Interlocked.Decrement(ref _count);

                yield return sender;
            }
            yield return new SocketPipeSender(_scheduler);
        }
    }
    public void Return(SocketPipeSender sender)
    {
        // This counting isn't accurate, but it's good enough for what we need to avoid using _queue.Count which could be expensive
        if (_disposed || Interlocked.Increment(ref _count) > MaxQueueSize)
        {
            Interlocked.Decrement(ref _count);
            sender.Dispose();
            return;
        }

        sender.Reset();
        _queue.Enqueue(sender);
    }
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            while (_queue.TryDequeue(out var sender))
            {
                sender.Dispose();
            }
        }
    }
}
