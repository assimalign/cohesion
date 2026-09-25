using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The platform-neutral realization plan compiled by a selected platform gateway.
/// </summary>
public sealed record ResourcePlan
{
    /// <summary>The schema identifier understood by this contract version.</summary>
    public const string CurrentSchema = "cohesion/plan/v1";

    /// <summary>Initializes a resource realization plan.</summary>
    /// <param name="schema">The plan schema identifier.</param>
    /// <param name="resource">The manifest resource name.</param>
    /// <param name="kind">The manifest resource kind.</param>
    /// <param name="workload">The workload controller and lifecycle specification.</param>
    /// <param name="container">The resource container specification.</param>
    /// <param name="volumes">Storage to materialize for the container.</param>
    /// <param name="services">Stable services to create for the workload.</param>
    /// <param name="exposures">Public endpoint exposures to create.</param>
    /// <param name="hints">
    /// Optional compiler hints. Unknown hints are advisory; unknown specification fields are not.
    /// </param>
    /// <exception cref="ArgumentNullException">Any specification or collection is <see langword="null"/>.</exception>
    public ResourcePlan(
        string schema,
        ResourceName resource,
        string kind,
        WorkloadSpec workload,
        ContainerSpec container,
        IReadOnlyList<VolumeSpec> volumes,
        IReadOnlyList<ServiceSpec> services,
        IReadOnlyList<ExposureSpec> exposures,
        IReadOnlyDictionary<string, string> hints)
        : this(
            schema,
            resource,
            kind,
            workload,
            container,
            volumes,
            services,
            exposures,
            hints,
            controlPlane: null)
    {
    }

    /// <summary>Initializes a resource realization plan with control-plane facts.</summary>
    /// <param name="schema">The plan schema identifier.</param>
    /// <param name="resource">The manifest resource name.</param>
    /// <param name="kind">The manifest resource kind.</param>
    /// <param name="workload">The workload controller and lifecycle specification.</param>
    /// <param name="container">The resource container specification.</param>
    /// <param name="volumes">Storage to materialize for the container.</param>
    /// <param name="services">Stable services to create for the workload.</param>
    /// <param name="exposures">Public endpoint exposures to create.</param>
    /// <param name="hints">Optional advisory compiler hints.</param>
    /// <param name="controlPlane">
    /// The resource's default control-plane location. A missing value represents a legacy plan
    /// that omitted control-plane facts.
    /// </param>
    /// <exception cref="ArgumentNullException">Any specification or collection is <see langword="null"/>.</exception>
    [JsonConstructor]
    public ResourcePlan(
        string schema,
        ResourceName resource,
        string kind,
        WorkloadSpec workload,
        ContainerSpec container,
        IReadOnlyList<VolumeSpec> volumes,
        IReadOnlyList<ServiceSpec> services,
        IReadOnlyList<ExposureSpec> exposures,
        IReadOnlyDictionary<string, string> hints,
        ControlPlaneSpec? controlPlane)
    {
        ArgumentNullException.ThrowIfNull(workload);
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(volumes);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(exposures);
        ArgumentNullException.ThrowIfNull(hints);

        Schema = schema;
        Resource = resource;
        Kind = kind;
        Workload = workload;
        Container = container;
        ControlPlane = controlPlane ?? new ControlPlaneSpec();
        Volumes = Copy(volumes);
        Services = Copy(services);
        Exposures = Copy(exposures);

        var hintCopy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string name, string value) in hints)
        {
            hintCopy.Add(name, value);
        }

        Hints = new ReadOnlyDictionary<string, string>(hintCopy);
    }

    /// <summary>Gets the plan schema identifier.</summary>
    public string Schema { get; }

    /// <summary>Gets the resource name this plan realizes.</summary>
    [JsonConverter(typeof(ResourceNameJsonConverter))]
    public ResourceName Resource { get; }

    /// <summary>Gets the manifest resource kind.</summary>
    public string Kind { get; }

    /// <summary>Gets the workload specification.</summary>
    public WorkloadSpec Workload { get; }

    /// <summary>Gets the resource container specification.</summary>
    public ContainerSpec Container { get; }

    /// <summary>Gets the resource's default control-plane location.</summary>
    public ControlPlaneSpec ControlPlane { get; }

    /// <summary>Gets the immutable volume specifications.</summary>
    public IReadOnlyList<VolumeSpec> Volumes { get; }

    /// <summary>Gets the immutable service specifications.</summary>
    public IReadOnlyList<ServiceSpec> Services { get; }

    /// <summary>Gets the immutable public exposure specifications.</summary>
    public IReadOnlyList<ExposureSpec> Exposures { get; }

    /// <summary>Gets the immutable advisory compiler hints.</summary>
    public IReadOnlyDictionary<string, string> Hints { get; }

    private static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> source)
    {
        var copy = new T[source.Count];
        for (int index = 0; index < source.Count; index++)
        {
            copy[index] = source[index];
        }

        return new ReadOnlyCollection<T>(copy);
    }
}
