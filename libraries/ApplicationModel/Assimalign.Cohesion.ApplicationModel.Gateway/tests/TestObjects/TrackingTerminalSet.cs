using System;
using System.Collections;
using System.Collections.Generic;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// A terminal set that can fail on a later membership check, proving an abandoned waiter no
/// longer retains and consults the set when another state is observed.
/// </summary>
internal sealed class TrackingTerminalSet : IReadOnlySet<ResourceLifecycle>
{
    private readonly HashSet<ResourceLifecycle> _states;

    public TrackingTerminalSet(params ResourceLifecycle[] states)
    {
        _states = new HashSet<ResourceLifecycle>(states);
    }

    public int Count => _states.Count;

    public bool ThrowOnContains { get; set; }

    public Action<ResourceLifecycle>? OnContains { get; set; }

    public bool Contains(ResourceLifecycle item)
    {
        OnContains?.Invoke(item);

        if (ThrowOnContains)
        {
            throw new InvalidOperationException("The terminal set was consulted after its wait completed.");
        }

        return _states.Contains(item);
    }

    public bool IsProperSubsetOf(IEnumerable<ResourceLifecycle> other) => _states.IsProperSubsetOf(other);

    public bool IsProperSupersetOf(IEnumerable<ResourceLifecycle> other) => _states.IsProperSupersetOf(other);

    public bool IsSubsetOf(IEnumerable<ResourceLifecycle> other) => _states.IsSubsetOf(other);

    public bool IsSupersetOf(IEnumerable<ResourceLifecycle> other) => _states.IsSupersetOf(other);

    public bool Overlaps(IEnumerable<ResourceLifecycle> other) => _states.Overlaps(other);

    public bool SetEquals(IEnumerable<ResourceLifecycle> other) => _states.SetEquals(other);

    public IEnumerator<ResourceLifecycle> GetEnumerator() => _states.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
