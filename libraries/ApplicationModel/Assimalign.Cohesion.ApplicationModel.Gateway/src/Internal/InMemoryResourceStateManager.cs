using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// The default in-memory <see cref="IApplicationResourceStateManager"/>: a level-triggered
/// store whose reads, writes, and waiter registration all happen under one lock, so a
/// <see cref="SetState"/> racing a <see cref="WaitForStateAsync"/> can never be lost.
/// </summary>
internal sealed class InMemoryResourceStateManager : IApplicationResourceStateManager
{
    // A value the enum never legitimately carries; used only to signal a wait budget/token elapsed.
    private const ResourceLifecycle TimedOut = (ResourceLifecycle)(-1);

    private readonly object _gate = new();
    private readonly Dictionary<ResourceId, Entry> _entries = new();

    public event EventHandler<ResourceStateChangedEventArgs>? StateChanged;

    public ResourceLifecycle GetState(ResourceId id)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(id, out Entry? entry) ? entry.State : ResourceLifecycle.Unknown;
        }
    }

    public IReadOnlyList<ResourceEndpoint> GetObservedEndpoints(ResourceId id)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(id, out Entry? entry) && entry.Endpoints is not null
                ? entry.Endpoints
                : Array.Empty<ResourceEndpoint>();
        }
    }

    public void SetState(
        ResourceId id,
        ResourceLifecycle state,
        string? detail = null,
        IReadOnlyList<ResourceEndpoint>? observedEndpoints = null)
    {
        ResourceLifecycle previous;
        List<Waiter>? completed = null;

        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out Entry? entry))
            {
                entry = new Entry();
                _entries[id] = entry;
            }

            previous = entry.State;
            entry.State = state;

            if (observedEndpoints is not null)
            {
                entry.Endpoints = observedEndpoints;
            }

            if (entry.Waiters is not null)
            {
                for (int i = entry.Waiters.Count - 1; i >= 0; i--)
                {
                    if (entry.Waiters[i].Terminals.Contains(state))
                    {
                        (completed ??= new List<Waiter>()).Add(entry.Waiters[i]);
                        entry.Waiters.RemoveAt(i);
                    }
                }
            }
        }

        // Complete waiters and raise the event outside the lock.
        if (completed is not null)
        {
            foreach (Waiter waiter in completed)
            {
                waiter.Completion.TrySetResult(state);
            }
        }

        if (previous != state)
        {
            StateChanged?.Invoke(this, new ResourceStateChangedEventArgs(id, previous, state, detail));
        }
    }

    public async Task<ResourceLifecycle> WaitForStateAsync(
        ResourceId id,
        IReadOnlySet<ResourceLifecycle> terminals,
        TimeSpan budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terminals);

        Waiter waiter;
        lock (_gate)
        {
            ResourceLifecycle current = _entries.TryGetValue(id, out Entry? existing)
                ? existing.State
                : ResourceLifecycle.Unknown;

            if (terminals.Contains(current))
            {
                return current;
            }

            if (existing is null)
            {
                existing = new Entry();
                _entries[id] = existing;
            }

            waiter = new Waiter(terminals);
            (existing.Waiters ??= new List<Waiter>()).Add(waiter);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (budget != Timeout.InfiniteTimeSpan)
        {
            timeout.CancelAfter(budget);
        }

        using (timeout.Token.Register(static state => ((Waiter)state!).Completion.TrySetResult(TimedOut), waiter))
        {
            ResourceLifecycle reached = await waiter.Completion.Task.ConfigureAwait(false);

            if (reached != TimedOut)
            {
                return reached;
            }

            // Budget or token elapsed: drop the waiter and report the last observed state.
            lock (_gate)
            {
                if (_entries.TryGetValue(id, out Entry? entry))
                {
                    entry.Waiters?.Remove(waiter);
                    return entry.State;
                }

                return ResourceLifecycle.Unknown;
            }
        }
    }

    private sealed class Entry
    {
        public ResourceLifecycle State { get; set; } = ResourceLifecycle.Unknown;

        public IReadOnlyList<ResourceEndpoint>? Endpoints { get; set; }

        public List<Waiter>? Waiters { get; set; }
    }

    private sealed class Waiter
    {
        public Waiter(IReadOnlySet<ResourceLifecycle> terminals)
        {
            Terminals = terminals;
        }

        public IReadOnlySet<ResourceLifecycle> Terminals { get; }

        public TaskCompletionSource<ResourceLifecycle> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
