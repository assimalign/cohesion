using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// An immutable observation of one manifest reference at the start of a reconcile pass.
/// </summary>
public sealed class ResourceDependencyObservation
{
    /// <summary>Initializes an observed dependency snapshot.</summary>
    /// <param name="application">The referenced application.</param>
    /// <param name="resource">The referenced resource.</param>
    /// <param name="state">The observed lifecycle state.</param>
    /// <param name="requestedEndpoints">The endpoint names requested by the manifest reference.</param>
    /// <param name="endpoints">The dependency's currently observed endpoints.</param>
    /// <param name="optional">Whether an unavailable observation may be omitted from input projection.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="requestedEndpoints"/> or <paramref name="endpoints"/> is <see langword="null"/>.
    /// </exception>
    public ResourceDependencyObservation(
        ApplicationName application,
        ResourceName resource,
        ResourceLifecycle state,
        IReadOnlyList<string> requestedEndpoints,
        IReadOnlyList<ResourceEndpoint> endpoints,
        bool optional)
    {
        ArgumentNullException.ThrowIfNull(requestedEndpoints);
        ArgumentNullException.ThrowIfNull(endpoints);
        Application = application;
        Resource = resource;
        State = state;
        RequestedEndpoints = Copy(requestedEndpoints);
        Endpoints = Copy(endpoints);
        Optional = optional;
    }

    /// <summary>Gets the referenced application.</summary>
    public ApplicationName Application { get; }

    /// <summary>Gets the referenced resource.</summary>
    public ResourceName Resource { get; }

    /// <summary>Gets the dependency state observed for this reconcile pass.</summary>
    public ResourceLifecycle State { get; }

    /// <summary>Gets the endpoint names requested by the manifest reference.</summary>
    public IReadOnlyList<string> RequestedEndpoints { get; }

    /// <summary>Gets the dependency's observed endpoints.</summary>
    public IReadOnlyList<ResourceEndpoint> Endpoints { get; }

    /// <summary>Gets whether the originating manifest reference is optional.</summary>
    public bool Optional { get; }

    private static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> source)
    {
        var copy = new T[source.Count];
        for (int index = 0; index < copy.Length; index++)
        {
            copy[index] = source[index];
        }

        return new ReadOnlyCollection<T>(copy);
    }
}
