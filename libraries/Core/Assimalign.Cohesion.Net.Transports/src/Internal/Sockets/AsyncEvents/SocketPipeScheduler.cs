using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Transports.Internal;

internal class SocketPipeScheduler : PipeScheduler, IThreadPoolWorkItem
{
    private readonly ConcurrentQueue<Work> queue;
    private int active;

    public SocketPipeScheduler()
    {
        this.queue = new ConcurrentQueue<Work>();
    }

    public override void Schedule(Action<object>? action, object? state)
    {
        queue.Enqueue(new Work(action!, state!));

        // Set working if it wasn't (via atomic Interlocked).
        if (Interlocked.CompareExchange(ref active, 1, 0) == 0)
        {
            // Wasn't working, schedule.
            System.Threading.ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
        }
    }

    void IThreadPoolWorkItem.Execute()
    {
        while (true)
        {
            while (queue.TryDequeue(out Work item))
            {
                item.Callback(item.State);
            }

            // All work done.

            // Set 'active' (0 == false) prior to checking IsEmpty to catch any missed work in interim.
            // This doesn't need to be volatile due to the following barrier (i.e. it is volatile).
            active = 0;

            // Ensure 'active' is written before IsEmpty is read.
            // As they are two different memory locations, we insert a barrier to guarantee ordering.
            Thread.MemoryBarrier();

            // Check if there is work to do
            if (queue.IsEmpty)
            {
                // Nothing to do, exit.
                break;
            }

            // Is work, can we set it as active again (via atomic Interlocked), prior to scheduling?
            if (Interlocked.Exchange(ref active, 1) == 1)
            {
                // Execute has been rescheduled already, exit.
                break;
            }

            // Is work, wasn't already scheduled so continue loop.
        }
    }

    private readonly struct Work
    {
        public readonly Action<object> Callback;
        public readonly object State;

        public Work(Action<object> callback, object state)
        {
            Callback = callback;
            State = state;
        }
    }
}