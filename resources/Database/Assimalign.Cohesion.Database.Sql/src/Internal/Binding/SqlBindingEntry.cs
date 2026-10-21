using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>One successful binding, with cancellable waiters and retry after an unsuccessful attempt.</summary>
internal sealed class SqlBindingEntry<T> where T : class
{
    private readonly object _sync = new();
    private readonly Func<CancellationToken, T>? _bind;
    private T? _value;
    private TaskCompletionSource? _pending;

    internal SqlBindingEntry(Func<CancellationToken, T> bind) { _bind = bind; }
    internal SqlBindingEntry(T value) { _value = value; }

    internal T? Value => Volatile.Read(ref _value);

    internal T Get(CancellationToken cancellationToken)
    {
        if (Value is { } cached) { return cached; }
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TaskCompletionSource pending;
            bool compute;
            lock (_sync)
            {
                if (_value is { } value) { return value; }
                compute = _pending is null;
                pending = _pending ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            if (!compute)
            {
                // Cancellation belongs to this waiter, not the computing statement. Completion
                // only signals a retry: a failed/canceled producer never exports its exception
                // (or token) to another statement and never poisons the entry.
                pending.Task.WaitAsync(cancellationToken).GetAwaiter().GetResult();
                continue;
            }

            try
            {
                // Folding may execute application code. No cache monitor is held here.
                T value = _bind!(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                Volatile.Write(ref _value, value);
                return value;
            }
            finally
            {
                lock (_sync) { _pending = null; }
                pending.SetResult();
            }
        }
    }
}
