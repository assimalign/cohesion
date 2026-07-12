using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.Database.ApplicationModel.Tests;

/// <summary>
/// A minimal in-memory <see cref="IApplicationResourceStateManager"/> for driving the
/// generic gateway algorithm in tests: level-triggered state with a race-free readiness
/// wait.
/// </summary>
internal sealed class RecordingStateManager : IApplicationResourceStateManager
{
    private readonly object _gate = new();
    private readonly Dictionary<ResourceId, ResourceLifecycle> _states = new();
    private readonly Dictionary<ResourceId, IReadOnlyList<ResourceEndpoint>> _endpoints = new();
    private readonly List<Waiter> _waiters = new();

    public event EventHandler<ResourceStateChangedEventArgs>? StateChanged;

    public ResourceLifecycle GetState(ResourceId id)
    {
        lock (_gate)
        {
            return _states.TryGetValue(id, out ResourceLifecycle state) ? state : ResourceLifecycle.Unknown;
        }
    }

    public IReadOnlyList<ResourceEndpoint> GetObservedEndpoints(ResourceId id)
    {
        lock (_gate)
        {
            return _endpoints.TryGetValue(id, out IReadOnlyList<ResourceEndpoint>? endpoints)
                ? endpoints
                : Array.Empty<ResourceEndpoint>();
        }
    }

    public void SetState(ResourceId id, ResourceLifecycle state, string? detail = null, IReadOnlyList<ResourceEndpoint>? observedEndpoints = null)
    {
        ResourceLifecycle previous;
        List<Waiter>? completed = null;

        lock (_gate)
        {
            previous = _states.TryGetValue(id, out ResourceLifecycle existing) ? existing : ResourceLifecycle.Unknown;
            _states[id] = state;

            if (observedEndpoints is not null)
            {
                _endpoints[id] = observedEndpoints;
            }

            for (int i = _waiters.Count - 1; i >= 0; i--)
            {
                if (_waiters[i].Id.Equals(id) && _waiters[i].Terminals.Contains(state))
                {
                    (completed ??= new List<Waiter>()).Add(_waiters[i]);
                    _waiters.RemoveAt(i);
                }
            }
        }

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

    public Task<ResourceLifecycle> WaitForStateAsync(ResourceId id, IReadOnlySet<ResourceLifecycle> terminals, TimeSpan budget, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terminals);

        Waiter waiter;
        lock (_gate)
        {
            ResourceLifecycle current = _states.TryGetValue(id, out ResourceLifecycle state) ? state : ResourceLifecycle.Unknown;

            if (terminals.Contains(current))
            {
                return Task.FromResult(current);
            }

            waiter = new Waiter(id, terminals);
            _waiters.Add(waiter);
        }

        return WaitAsync(waiter, budget, cancellationToken);
    }

    private async Task<ResourceLifecycle> WaitAsync(Waiter waiter, TimeSpan budget, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (budget != Timeout.InfiniteTimeSpan)
        {
            timeout.CancelAfter(budget);
        }

        using (timeout.Token.Register(static state => ((Waiter)state!).Completion.TrySetResult((ResourceLifecycle)(-1)), waiter))
        {
            ResourceLifecycle reached = await waiter.Completion.Task.ConfigureAwait(false);

            if (reached != (ResourceLifecycle)(-1))
            {
                return reached;
            }

            lock (_gate)
            {
                _waiters.Remove(waiter);
                return _states.TryGetValue(waiter.Id, out ResourceLifecycle state) ? state : ResourceLifecycle.Unknown;
            }
        }
    }

    private sealed class Waiter
    {
        public Waiter(ResourceId id, IReadOnlySet<ResourceLifecycle> terminals)
        {
            Id = id;
            Terminals = terminals;
        }

        public ResourceId Id { get; }

        public IReadOnlySet<ResourceLifecycle> Terminals { get; }

        public TaskCompletionSource<ResourceLifecycle> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
