using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
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

    private static readonly IReadOnlyDictionary<string, JsonElement> _emptyMap =
        new ReadOnlyDictionary<string, JsonElement>(new Dictionary<string, JsonElement>(StringComparer.Ordinal));

    private readonly IReadOnlyDictionary<string, JsonElement> _configuration = _emptyMap;
    private readonly IReadOnlyDictionary<string, JsonElement> _extensions = _emptyMap;

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

    /// <summary>Gets the optional application-owned logical group; it has no deployment semantics.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResourceGroupName? ResourceGroup { get; init; }

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

    /// <summary>
    /// Gets the resource's deployment layer: the RFC 7396 merge patch the application's code declares for each
    /// configuration section, keyed by section path. The gateway delivers it as the deployment record's
    /// <c>deployment.json</c>; an empty map delivers no deployment layer.
    /// </summary>
    /// <exception cref="ArgumentException">An assigned patch is not a JSON value.</exception>
    [JsonIgnore]
    public IReadOnlyDictionary<string, JsonElement> Configuration
    {
        get => _configuration;
        init => _configuration = Freeze(value, ErrorMessages.ResourceConfigurationPatchInvalid);
    }

    /// <summary>
    /// Gets platform extension data keyed by platform-qualified name, such as <c>kubernetes/hpa</c>. The gateway named
    /// by a key's platform part compiles the data and refuses a key it does not know; every other gateway ignores it.
    /// The data is plain JSON, so it survives describe and export round trips.
    /// </summary>
    /// <exception cref="ArgumentException">An assigned value is not a JSON value.</exception>
    [JsonIgnore]
    public IReadOnlyDictionary<string, JsonElement> Extensions
    {
        get => _extensions;
        init => _extensions = Freeze(value, ErrorMessages.ResourceExtensionValueInvalid);
    }

    // The wire form omits both maps when they are empty, so a plan without configuration or extensions serializes
    // exactly as before they existed.
    [JsonInclude]
    [JsonPropertyName("configuration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal IReadOnlyDictionary<string, JsonElement>? ConfigurationDocument
    {
        get => _configuration.Count == 0 ? null : _configuration;
        init => _configuration = Freeze(value, ErrorMessages.ResourceConfigurationPatchInvalid);
    }

    [JsonInclude]
    [JsonPropertyName("extensions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal IReadOnlyDictionary<string, JsonElement>? ExtensionsDocument
    {
        get => _extensions.Count == 0 ? null : _extensions;
        init => _extensions = Freeze(value, ErrorMessages.ResourceExtensionValueInvalid);
    }

    private static IReadOnlyDictionary<string, JsonElement> Freeze(IReadOnlyDictionary<string, JsonElement>? source, string invalid)
    {
        if (source is null || source.Count == 0)
        {
            return _emptyMap;
        }

        var copy = new Dictionary<string, JsonElement>(source.Count, StringComparer.Ordinal);
        foreach ((string key, JsonElement value) in source)
        {
            if (value.ValueKind == JsonValueKind.Undefined)
            {
                throw new ArgumentException(invalid, nameof(source));
            }

            copy.Add(key, value.Clone());
        }

        return new ReadOnlyDictionary<string, JsonElement>(copy);
    }

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
