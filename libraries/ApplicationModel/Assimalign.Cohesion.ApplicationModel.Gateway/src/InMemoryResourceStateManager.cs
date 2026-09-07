using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

// Deviates from the repo interface-first rule per signed-off design item 18: the concrete
// reference state manager is intentionally public alongside its existing interface contract.
/// <summary>
/// The reference in-memory <see cref="IApplicationResourceStateManager"/>: a level-triggered
/// store whose reads, writes, and waiter registration all happen under one lock, so a
/// <see cref="SetState"/> racing a <see cref="WaitForStateAsync"/> can never be lost.
/// </summary>
public sealed class InMemoryResourceStateManager : IApplicationResourceStateManager
{
    private readonly object _gate = new();
    private readonly Dictionary<ResourceId, Entry> _entries = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryResourceStateManager"/> class.
    /// </summary>
    public InMemoryResourceStateManager()
    {
    }

    /// <inheritdoc/>
    public event EventHandler<ResourceStateChangedEventArgs>? StateChanged;

    /// <inheritdoc/>
    public ResourceLifecycle GetState(ResourceId id)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(id, out Entry? entry) ? entry.State : ResourceLifecycle.Unknown;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<ResourceEndpoint> GetObservedEndpoints(ResourceId id)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(id, out Entry? entry) && entry.Endpoints is not null
                ? entry.Endpoints
                : Array.Empty<ResourceEndpoint>();
        }
    }

    /// <inheritdoc/>
    public void SetState(
        ResourceId id,
        ResourceLifecycle state,
        string? detail = null,
        IReadOnlyList<ResourceEndpoint>? observedEndpoints = null)
    {
        ResourceLifecycle previous;
        bool detailChanged;
        List<Waiter>? completed = null;

        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out Entry? entry))
            {
                entry = new Entry();
                _entries[id] = entry;
            }

            previous = entry.State;
            detailChanged = !string.Equals(entry.Detail, detail, StringComparison.Ordinal);
            entry.State = state;
            entry.Detail = detail;

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

                if (entry.Waiters.Count == 0)
                {
                    entry.Waiters = null;
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

        if (previous != state || detailChanged)
        {
            StateChanged?.Invoke(this, new ResourceStateChangedEventArgs(id, previous, state, detail));
        }
    }

    /// <inheritdoc/>
    public async Task<ResourceLifecycle> WaitForStateAsync(
        ResourceId id,
        IReadOnlySet<ResourceLifecycle> terminals,
        TimeSpan budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terminals);
        cancellationToken.ThrowIfCancellationRequested();
        using CancellationTokenSource? timeout = budget == Timeout.InfiniteTimeSpan
            ? null
            : new CancellationTokenSource(budget);

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

            waiter = new Waiter(this, id, terminals, cancellationToken);
            (existing.Waiters ??= new List<Waiter>()).Add(waiter);
        }

        try
        {
            using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(
                static state => ((Waiter)state!).Cancel(),
                waiter);
            using CancellationTokenRegistration timeoutRegistration = timeout is null
                ? default
                : timeout.Token.Register(
                    static state => ((Waiter)state!).Timeout(),
                    waiter);

            return await waiter.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            TryRemoveWaiter(waiter, out _);
        }
    }

    private void CancelWaiter(Waiter waiter)
    {
        if (TryRemoveWaiter(waiter, out _))
        {
            waiter.Completion.TrySetCanceled(waiter.CancellationToken);
        }
    }

    private void TimeoutWaiter(Waiter waiter)
    {
        if (TryRemoveWaiter(waiter, out ResourceLifecycle current))
        {
            waiter.Completion.TrySetResult(current);
        }
    }

    private bool TryRemoveWaiter(Waiter waiter, out ResourceLifecycle current)
    {
        lock (_gate)
        {
            current = ResourceLifecycle.Unknown;
            if (!_entries.TryGetValue(waiter.Id, out Entry? entry) || entry.Waiters is null)
            {
                return false;
            }

            current = entry.State;
            if (!entry.Waiters.Remove(waiter))
            {
                return false;
            }

            if (entry.Waiters.Count == 0)
            {
                entry.Waiters = null;
            }

            return true;
        }
    }

    private sealed class Entry
    {
        public ResourceLifecycle State { get; set; } = ResourceLifecycle.Unknown;

        public IReadOnlyList<ResourceEndpoint>? Endpoints { get; set; }

        public string? Detail { get; set; }

        public List<Waiter>? Waiters { get; set; }
    }

    private sealed class Waiter
    {
        public Waiter(
            InMemoryResourceStateManager owner,
            ResourceId id,
            IReadOnlySet<ResourceLifecycle> terminals,
            CancellationToken cancellationToken)
        {
            Owner = owner;
            Id = id;
            Terminals = terminals;
            CancellationToken = cancellationToken;
        }

        public InMemoryResourceStateManager Owner { get; }

        public ResourceId Id { get; }

        public IReadOnlySet<ResourceLifecycle> Terminals { get; }

        public CancellationToken CancellationToken { get; }

        public TaskCompletionSource<ResourceLifecycle> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Cancel() => Owner.CancelWaiter(this);

        public void Timeout() => Owner.TimeoutWaiter(this);
    }
}
