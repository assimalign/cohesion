using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

/// <summary>Runs test actors independently of the pool, including their ordinary await continuations.</summary>
internal static class DedicatedTestTask
{
    private const TaskCreationOptions options = TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach;

    internal static Task Run(Action action, CancellationToken cancellationToken = default)
        => Task.Factory.StartNew(action, cancellationToken, options, TaskScheduler.Default);

    internal static Task<T> Run<T>(Func<T> action, CancellationToken cancellationToken = default)
        => Task.Factory.StartNew(action, cancellationToken, options, TaskScheduler.Default);

    internal static Task Run(Func<Task> action, CancellationToken cancellationToken = default)
        => Run(async () => { await action(); return true; }, cancellationToken);

    internal static async Task<T> Run<T>(Func<Task<T>> action, CancellationToken cancellationToken = default)
        => await Task.Factory.StartNew(() =>
        {
            using var scheduler = new ActorScheduler();
            var result = Task.Factory.StartNew(async () => await action(), cancellationToken,
                TaskCreationOptions.DenyChildAttach, scheduler).Unwrap();
            scheduler.Complete(result);
            return result.GetAwaiter().GetResult();
        }, cancellationToken, options, TaskScheduler.Default).ConfigureAwait(false);

    // A LongRunning async delegate alone returns its thread at the first incomplete await.
    // Keep driver continuations on the actor's thread without installing a synchronization context.
    private sealed class ActorScheduler : TaskScheduler, IDisposable
    {
        private readonly object _sync = new();
        private readonly LinkedList<Task> _pending = new();
        private readonly AutoResetEvent _available = new(false);
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private bool _closed;

        protected override void QueueTask(Task task)
        {
            lock (_sync)
            {
                if (!_closed)
                {
                    _pending.AddLast(task);
                    _available.Set();
                    return;
                }
            }
            // Detached work may outlive its actor; it no longer owns a dedicated driver.
            _ = Task.Factory.StartNew(() => TryExecuteTask(task), CancellationToken.None,
                TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        {
            if (Environment.CurrentManagedThreadId != _thread) { return false; }
            if (taskWasPreviouslyQueued && !TryDequeue(task)) { return false; }
            return TryExecuteTask(task);
        }

        protected override bool TryDequeue(Task task)
        {
            lock (_sync) { return _pending.Remove(task); }
        }

        protected override IEnumerable<Task> GetScheduledTasks()
        {
            lock (_sync) { return _pending.ToArray(); }
        }

        internal void Complete(Task result)
        {
            // Task completion signals its handle without requiring a pool continuation.
            WaitHandle[] ready = [_available, ((IAsyncResult)result).AsyncWaitHandle];
            while (!result.IsCompleted)
            {
                Task? next = null;
                lock (_sync)
                {
                    if (_pending.First is { } first)
                    {
                        next = first.Value;
                        _pending.RemoveFirst();
                    }
                }
                if (next is not null) { TryExecuteTask(next); }
                else { WaitHandle.WaitAny(ready); }
            }
        }

        public void Dispose()
        {
            Task[] detached;
            lock (_sync)
            {
                _closed = true;
                detached = _pending.ToArray();
                _pending.Clear();
            }
            foreach (var task in detached) { QueueTask(task); }
            _available.Dispose();
        }
    }
}
