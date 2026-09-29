using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Defines the terminal lifecycle states that complete an initial readiness wait and the
/// subset of those states that satisfy the wait.
/// </summary>
public sealed record ReadinessGate
{
    /// <summary>Initializes an immutable readiness gate.</summary>
    /// <param name="terminals">States that complete the wait.</param>
    /// <param name="satisfying">Terminal states that count as readiness success.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="terminals"/> or <paramref name="satisfying"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A collection is empty, contains duplicates, or a satisfying state is not terminal.
    /// </exception>
    public ReadinessGate(
        IReadOnlyList<ResourceLifecycle> terminals,
        IReadOnlyList<ResourceLifecycle> satisfying)
    {
        ArgumentNullException.ThrowIfNull(terminals);
        ArgumentNullException.ThrowIfNull(satisfying);

        if (terminals.Count is 0)
        {
            throw new ArgumentException("A readiness gate must declare at least one terminal state.", nameof(terminals));
        }

        if (satisfying.Count is 0)
        {
            throw new ArgumentException("A readiness gate must declare at least one satisfying state.", nameof(satisfying));
        }

        var terminalSet = new HashSet<ResourceLifecycle>();
        var terminalCopy = new ResourceLifecycle[terminals.Count];
        for (int index = 0; index < terminals.Count; index++)
        {
            ResourceLifecycle state = terminals[index];
            if (!terminalSet.Add(state))
            {
                throw new ArgumentException($"Terminal state '{state}' is declared more than once.", nameof(terminals));
            }

            terminalCopy[index] = state;
        }

        var satisfyingSet = new HashSet<ResourceLifecycle>();
        var satisfyingCopy = new ResourceLifecycle[satisfying.Count];
        for (int index = 0; index < satisfying.Count; index++)
        {
            ResourceLifecycle state = satisfying[index];
            if (!terminalSet.Contains(state))
            {
                throw new ArgumentException($"Satisfying state '{state}' is not a terminal state.", nameof(satisfying));
            }

            if (!satisfyingSet.Add(state))
            {
                throw new ArgumentException($"Satisfying state '{state}' is declared more than once.", nameof(satisfying));
            }

            satisfyingCopy[index] = state;
        }

        Terminals = new ReadOnlyCollection<ResourceLifecycle>(terminalCopy);
        Satisfying = new ReadOnlyCollection<ResourceLifecycle>(satisfyingCopy);
    }

    /// <summary>Gets the immutable lifecycle states that complete the readiness wait.</summary>
    public IReadOnlyList<ResourceLifecycle> Terminals { get; }

    /// <summary>Gets the immutable terminal states that represent readiness success.</summary>
    public IReadOnlyList<ResourceLifecycle> Satisfying { get; }

    /// <summary>Creates the readiness gate defined for a workload kind.</summary>
    /// <param name="kind">The workload kind whose readiness semantics are required.</param>
    /// <returns>The immutable gate for <paramref name="kind"/>.</returns>
    public static ReadinessGate For(WorkloadKind kind) => kind switch
    {
        WorkloadKind.Job => new(
            [ResourceLifecycle.Stopped, ResourceLifecycle.Failed],
            [ResourceLifecycle.Stopped]),
        _ => new(
            [ResourceLifecycle.Running, ResourceLifecycle.Failed, ResourceLifecycle.Stopped],
            [ResourceLifecycle.Running])
    };
}
