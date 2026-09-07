using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Represents the generic, platform-neutral manifest emitted for a Cohesion resource.
/// </summary>
public sealed record ResourceManifest
{
    /// <summary>
    /// The only resource-manifest schema version supported by this contract.
    /// </summary>
    public const string SchemaV1 = "cohesion/resource/v1";

    /// <summary>
    /// Gets the resource-manifest schema identifier.
    /// </summary>
    [JsonRequired]
    public string Schema { get; init; } = SchemaV1;

    /// <summary>
    /// Gets the resource's stable name.
    /// </summary>
    [JsonConverter(typeof(ResourceNameJsonConverter))]
    public ResourceName Name { get; init; }

    /// <summary>
    /// Gets the resource-area kind.
    /// </summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>
    /// Gets the application that owns the resource.
    /// </summary>
    [JsonConverter(typeof(ApplicationNameJsonConverter))]
    public ApplicationName Application { get; init; }

    /// <summary>
    /// Gets the assembly identity of the resource area's ApplicationModel package.
    /// </summary>
    public string ApplicationModel { get; init; } = string.Empty;

    /// <summary>
    /// Gets the artifacts from which the resource can be realized.
    /// </summary>
    public ResourceManifestArtifact Artifact { get; init; } = new();

    /// <summary>
    /// Gets the endpoints exposed by the resource.
    /// </summary>
    public IReadOnlyList<ResourceManifestEndpoint> Endpoints { get; init; }
        = Array.Empty<ResourceManifestEndpoint>();

    /// <summary>
    /// Gets the resource's readiness, liveness, and startup probes.
    /// </summary>
    public ResourceManifestProbes Probes { get; init; } = new();

    /// <summary>
    /// Gets the resource area's default control-plane location.
    /// </summary>
    public ResourceManifestControlPlane ControlPlane { get; init; } = new();

    /// <summary>
    /// Gets the data mounts required by the resource.
    /// </summary>
    public IReadOnlyList<ResourceManifestMount> Mounts { get; init; }
        = Array.Empty<ResourceManifestMount>();

    /// <summary>
    /// Gets the typed configuration settings exposed by the resource.
    /// </summary>
    public IReadOnlyList<ResourceManifestSetting> Settings { get; init; }
        = Array.Empty<ResourceManifestSetting>();

    /// <summary>
    /// Gets the other resources consumed by this resource.
    /// </summary>
    public IReadOnlyList<ResourceManifestReference> References { get; init; }
        = Array.Empty<ResourceManifestReference>();

    /// <summary>
    /// Gets the command kinds accepted by the resource's default control plane.
    /// </summary>
    public IReadOnlyList<ResourceManifestCommand> Commands { get; init; }
        = Array.Empty<ResourceManifestCommand>();

    /// <summary>
    /// Gets additional environment variables declared by the resource.
    /// </summary>
    [JsonPropertyName("environment")]
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; }
        = new Dictionary<string, string>();

    /// <summary>
    /// Gets the resource's lifecycle requirements.
    /// </summary>
    public ResourceManifestLifecycle Lifecycle { get; init; } = new();

    /// <summary>
    /// Gets area-specific facts whose keys are prefixed by the lower-case resource kind.
    /// </summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; }
        = new Dictionary<string, string>();

    /// <summary>
    /// Parses and validates a resource manifest from JSON text.
    /// </summary>
    /// <param name="json">The JSON document to parse.</param>
    /// <returns>The parsed and validated resource manifest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The document is not valid resource-manifest JSON.</exception>
    /// <exception cref="InvalidDataException">The document violates the resource-manifest contract.</exception>
    public static ResourceManifest Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        ResourceManifest? manifest = JsonSerializer.Deserialize(
            json,
            ResourceManifestJsonContext.Default.ResourceManifest);

        return ValidateDeserialized(manifest);
    }

    /// <summary>
    /// Loads and validates a resource manifest from a stream.
    /// </summary>
    /// <param name="stream">The readable JSON stream. The method does not close the stream.</param>
    /// <returns>The loaded and validated resource manifest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The stream does not contain valid resource-manifest JSON.</exception>
    /// <exception cref="InvalidDataException">The document violates the resource-manifest contract.</exception>
    public static ResourceManifest Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        ResourceManifest? manifest = JsonSerializer.Deserialize(
            stream,
            ResourceManifestJsonContext.Default.ResourceManifest);

        return ValidateDeserialized(manifest);
    }

    /// <summary>
    /// Loads and validates a resource manifest from a file.
    /// </summary>
    /// <param name="path">The manifest file path.</param>
    /// <returns>The loaded and validated resource manifest.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The manifest file cannot be read.</exception>
    /// <exception cref="JsonException">The file does not contain valid resource-manifest JSON.</exception>
    /// <exception cref="InvalidDataException">The document violates the resource-manifest contract.</exception>
    public static ResourceManifest Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using FileStream stream = File.OpenRead(path);
        return Load(stream);
    }

    /// <summary>
    /// Validates this manifest against the supported resource-manifest contract.
    /// </summary>
    /// <returns>This manifest after successful validation.</returns>
    /// <exception cref="InvalidDataException">The manifest violates the resource-manifest contract.</exception>
    public ResourceManifest Validate()
    {
        if (!string.Equals(Schema, SchemaV1, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Resource manifest schema '{Schema}' is not supported. Expected '{SchemaV1}'.");
        }

        if (string.IsNullOrWhiteSpace(Name.ToString()))
        {
            throw new InvalidDataException("Resource manifest name must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Kind))
        {
            throw new InvalidDataException("Resource manifest kind must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Application.ToString()))
        {
            throw new InvalidDataException("Resource manifest application must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(ApplicationModel))
        {
            throw new InvalidDataException("Resource manifest applicationModel must not be empty.");
        }

        if (Artifact is null)
        {
            throw new InvalidDataException("Resource manifest artifact must not be null.");
        }

        if (string.IsNullOrWhiteSpace(Artifact.Assembly))
        {
            throw new InvalidDataException("Resource manifest artifact assembly must not be empty.");
        }

        HashSet<string> endpointNames = ValidateEndpoints();

        if (Probes is null)
        {
            throw new InvalidDataException("Resource manifest probes must not be null.");
        }

        ValidateProbe("readiness", Probes.Readiness, endpointNames);
        ValidateProbe("liveness", Probes.Liveness, endpointNames);
        ValidateProbe("startup", Probes.Startup, endpointNames);

        if (ControlPlane is null)
        {
            throw new InvalidDataException("Resource manifest controlPlane must not be null.");
        }

        if (string.IsNullOrWhiteSpace(ControlPlane.Endpoint)
            || !endpointNames.Contains(ControlPlane.Endpoint))
        {
            throw new InvalidDataException(
                $"Resource manifest control-plane endpoint '{ControlPlane.Endpoint}' does not name a declared endpoint.");
        }

        if (string.IsNullOrWhiteSpace(ControlPlane.Path))
        {
            throw new InvalidDataException("Resource manifest control-plane path must not be empty.");
        }

        string propertyPrefix = Kind.ToLowerInvariant() + ".";

        if (Properties is null)
        {
            throw new InvalidDataException("Resource manifest properties must not be null.");
        }

        foreach ((string propertyName, string propertyValue) in Properties)
        {
            if (!propertyName.StartsWith(propertyPrefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Resource manifest property '{propertyName}' must use the '{propertyPrefix}' prefix for resource kind '{Kind}'.");
            }

            if (propertyValue is null)
            {
                throw new InvalidDataException(
                    $"Resource manifest property '{propertyName}' must not have a null value.");
            }
        }

        if (Mounts is null)
        {
            throw new InvalidDataException("Resource manifest mounts must not be null.");
        }

        var mountNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (ResourceManifestMount mount in Mounts)
        {
            if (mount is null)
            {
                throw new InvalidDataException("Resource manifest mounts must not contain null entries.");
            }

            if (string.IsNullOrWhiteSpace(mount.Name))
            {
                throw new InvalidDataException("Resource manifest mount names must not be empty.");
            }

            if (!mountNames.Add(mount.Name))
            {
                throw new InvalidDataException(
                    $"Resource manifest contains duplicate mount name '{mount.Name}'.");
            }

            if (!Enum.IsDefined(mount.Kind))
            {
                throw new InvalidDataException(
                    $"Resource manifest mount '{mount.Name}' has unsupported kind '{mount.Kind}'.");
            }

            if (string.IsNullOrWhiteSpace(mount.ContainerPath))
            {
                throw new InvalidDataException(
                    $"Resource manifest mount '{mount.Name}' must declare a non-empty containerPath.");
            }

            if (mount.Kind == ResourceMountKind.Volume && string.IsNullOrWhiteSpace(mount.Size))
            {
                throw new InvalidDataException(
                    $"Volume mount '{mount.Name}' must declare a non-empty size.");
            }
        }

        if (Settings is null)
        {
            throw new InvalidDataException("Resource manifest settings must not be null.");
        }

        foreach (ResourceManifestSetting setting in Settings)
        {
            if (setting is null || string.IsNullOrWhiteSpace(setting.Key))
            {
                throw new InvalidDataException("Resource manifest setting keys must not be empty.");
            }
        }

        if (References is null)
        {
            throw new InvalidDataException("Resource manifest references must not be null.");
        }

        foreach (ResourceManifestReference reference in References)
        {
            ValidateReference(reference);
        }

        if (Commands is null)
        {
            throw new InvalidDataException("Resource manifest commands must not be null.");
        }

        foreach (ResourceManifestCommand command in Commands)
        {
            if (command is null || string.IsNullOrWhiteSpace(command.Kind))
            {
                throw new InvalidDataException("Resource manifest command kinds must not be empty.");
            }
        }

        if (EnvironmentVariables is null)
        {
            throw new InvalidDataException("Resource manifest environment must not be null.");
        }

        foreach (KeyValuePair<string, string> variable in EnvironmentVariables)
        {
            if (string.IsNullOrWhiteSpace(variable.Key) || variable.Value is null)
            {
                throw new InvalidDataException(
                    "Resource manifest environment variable names must be non-empty and values must not be null.");
            }
        }

        ValidateLifecycle();

        return this;
    }

    private HashSet<string> ValidateEndpoints()
    {
        if (Endpoints is null)
        {
            throw new InvalidDataException("Resource manifest endpoints must not be null.");
        }

        var endpointNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (ResourceManifestEndpoint endpoint in Endpoints)
        {
            if (endpoint is null)
            {
                throw new InvalidDataException("Resource manifest endpoints must not contain null entries.");
            }

            if (string.IsNullOrWhiteSpace(endpoint.Name))
            {
                throw new InvalidDataException("Resource manifest endpoint names must not be empty.");
            }

            if (!endpointNames.Add(endpoint.Name))
            {
                throw new InvalidDataException(
                    $"Resource manifest contains duplicate endpoint name '{endpoint.Name}'.");
            }

            if (string.IsNullOrWhiteSpace(endpoint.Scheme))
            {
                throw new InvalidDataException(
                    $"Resource manifest endpoint '{endpoint.Name}' must declare a non-empty scheme.");
            }

            if (!string.Equals(endpoint.Protocol, "tcp", StringComparison.Ordinal)
                && !string.Equals(endpoint.Protocol, "udp", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Resource manifest endpoint '{endpoint.Name}' has unsupported protocol '{endpoint.Protocol}'; expected 'tcp' or 'udp'.");
            }

            ValidatePort(endpoint.Name, "containerPort", endpoint.ContainerPort);

            if (endpoint.DevPort is int devPort)
            {
                ValidatePort(endpoint.Name, "devPort", devPort);
            }
        }

        return endpointNames;
    }

    private static void ValidatePort(string endpointName, string memberName, int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new InvalidDataException(
                $"Resource manifest endpoint '{endpointName}' has invalid {memberName} '{port}'; expected a value from 1 through 65535.");
        }
    }

    private static void ValidateProbe(
        string role,
        ResourceManifestProbe? probe,
        IReadOnlySet<string> endpointNames)
    {
        if (probe is null)
        {
            return;
        }

        int mechanismCount = 0;
        mechanismCount += probe.Http is null ? 0 : 1;
        mechanismCount += probe.Tcp is null ? 0 : 1;
        mechanismCount += probe.Exec is null ? 0 : 1;
        mechanismCount += probe.Grpc is null ? 0 : 1;
        mechanismCount += probe.None is null ? 0 : 1;

        if (mechanismCount != 1)
        {
            throw new InvalidDataException(
                $"Resource manifest {role} probe must declare exactly one of http, tcp, exec, grpc, or none.");
        }

        bool requiresEndpoint = probe.Http is not null || probe.Tcp is not null || probe.Grpc is not null;
        if (requiresEndpoint && string.IsNullOrWhiteSpace(probe.Endpoint))
        {
            throw new InvalidDataException(
                $"Resource manifest {role} probe must name an endpoint for its network probe mechanism.");
        }

        if (probe.Endpoint is not null &&
            (string.IsNullOrWhiteSpace(probe.Endpoint) || !endpointNames.Contains(probe.Endpoint)))
        {
            throw new InvalidDataException(
                $"Resource manifest {role} probe endpoint '{probe.Endpoint}' does not name a declared endpoint.");
        }

        if (probe.Http is not null && string.IsNullOrWhiteSpace(probe.Http))
        {
            throw new InvalidDataException(
                $"Resource manifest {role} HTTP probe path must not be empty.");
        }

        if (probe.Tcp is false)
        {
            throw new InvalidDataException(
                $"Resource manifest {role} TCP probe must be true when declared.");
        }

        if (probe.Exec is not null
            && (probe.Exec.Count == 0 || string.IsNullOrWhiteSpace(probe.Exec[0])))
        {
            throw new InvalidDataException(
                $"Resource manifest {role} exec probe must declare a non-empty command.");
        }

        if (probe.Grpc is not null && string.IsNullOrWhiteSpace(probe.Grpc))
        {
            throw new InvalidDataException(
                $"Resource manifest {role} gRPC probe service must not be empty.");
        }

        if (probe.None is false)
        {
            throw new InvalidDataException(
                $"Resource manifest {role} none probe must be true when declared.");
        }
    }

    private static void ValidateReference(ResourceManifestReference? reference)
    {
        if (reference is null)
        {
            throw new InvalidDataException("Resource manifest references must not contain null entries.");
        }

        if (string.IsNullOrWhiteSpace(reference.Resource.ToString()))
        {
            throw new InvalidDataException("Resource manifest reference resource names must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(reference.Application.ToString()))
        {
            throw new InvalidDataException(
                $"Resource manifest reference '{reference.Resource}' must declare an application.");
        }

        if (string.IsNullOrWhiteSpace(reference.Manifest))
        {
            throw new InvalidDataException(
                $"Resource manifest reference '{reference.Resource}' must declare a manifest identity.");
        }

        if (reference.Endpoints is null)
        {
            throw new InvalidDataException(
                $"Resource manifest reference '{reference.Resource}' endpoints must not be null.");
        }

        foreach (string endpoint in reference.Endpoints)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new InvalidDataException(
                    $"Resource manifest reference '{reference.Resource}' endpoint names must not be empty.");
            }
        }
    }

    private void ValidateLifecycle()
    {
        if (Lifecycle is null)
        {
            throw new InvalidDataException("Resource manifest lifecycle must not be null.");
        }

        if (!Enum.IsDefined(Lifecycle.Workload))
        {
            throw new InvalidDataException(
                $"Resource manifest lifecycle has unsupported workload '{Lifecycle.Workload}'.");
        }

        if (Lifecycle.Replicas <= 0)
        {
            throw new InvalidDataException("Resource manifest lifecycle replicas must be positive.");
        }

        if (Lifecycle.MaxReplicas is int maxReplicas)
        {
            if (maxReplicas <= 0)
            {
                throw new InvalidDataException("Resource manifest lifecycle maxReplicas must be positive when declared.");
            }

            if (Lifecycle.Replicas > maxReplicas)
            {
                throw new InvalidDataException(
                    "Resource manifest lifecycle replicas must not exceed maxReplicas.");
            }
        }

        if (Lifecycle.StopGraceSeconds <= 0)
        {
            throw new InvalidDataException("Resource manifest lifecycle stopGraceSeconds must be positive.");
        }

        if (string.IsNullOrWhiteSpace(Lifecycle.RestartPolicy))
        {
            throw new InvalidDataException("Resource manifest lifecycle restartPolicy must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Lifecycle.ExitCodes))
        {
            throw new InvalidDataException("Resource manifest lifecycle exitCodes must not be empty.");
        }
    }

    private static ResourceManifest ValidateDeserialized(ResourceManifest? manifest)
    {
        if (manifest is null)
        {
            throw new InvalidDataException("The resource manifest document must contain a JSON object.");
        }

        return manifest.Validate();
    }
}
