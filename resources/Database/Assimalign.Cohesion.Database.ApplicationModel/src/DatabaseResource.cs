using System;
using System.Collections.Generic;
using System.Globalization;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.Database.ApplicationModel;

/// <summary>
/// The orchestration manifest for a Cohesion database: an executable resource that
/// exposes the database wire-protocol endpoint and mounts a persistent data volume.
/// </summary>
/// <remarks>
/// This type describes the database to a gateway; it carries no runtime behavior (the
/// manifest project references the declarative ApplicationModel plane only, never the
/// database runtime). The artifact name resolves to the standalone database host
/// executable on the local gateway and to its pre-built container image on container
/// gateways. The conventional environment variables it injects
/// (<see cref="DataPathVariable"/>, <see cref="PortVariable"/>,
/// <see cref="DurabilityVariable"/>) are the same names <c>Database.Hosting</c>'s
/// <c>DatabaseHostConfiguration</c> binds, so the manifest and the host agree by
/// convention without sharing an assembly.
/// </remarks>
public sealed class DatabaseResource : IApplicationResource, IExecutableResource, IEndpointResource, IMountResource
{
    /// <summary>
    /// The environment variable naming the directory the engine stores its data in.
    /// </summary>
    public const string DataPathVariable = "COHESION_DATABASE_DATA_PATH";

    /// <summary>
    /// The environment variable naming the port the wire-protocol endpoint listens on.
    /// </summary>
    public const string PortVariable = "COHESION_DATABASE_ENDPOINT_PORT";

    /// <summary>
    /// The environment variable naming the durability mode applied to the engine.
    /// </summary>
    public const string DurabilityVariable = "COHESION_DATABASE_DURABILITY";

    /// <summary>
    /// The logical name of the wire-protocol endpoint declared by this resource.
    /// </summary>
    public const string EndpointName = "db";

    /// <summary>
    /// The scheme of the wire-protocol endpoint declared by this resource.
    /// </summary>
    public const string EndpointScheme = "cohesion-db";

    private readonly DatabaseResourceOptions _options;
    private readonly IReadOnlyDictionary<string, string> _environmentVariables;

    /// <summary>
    /// Initializes a new <see cref="DatabaseResource"/>.
    /// </summary>
    /// <param name="name">The resource name, unique within the application.</param>
    /// <param name="options">Options shaping the declared endpoint and mounts; defaults are used when null.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is null or empty.</exception>
    public DatabaseResource(string name, DatabaseResourceOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Resource name cannot be null or empty.", nameof(name));
        }
        _options = options ?? new DatabaseResourceOptions();
        _environmentVariables = BuildEnvironmentVariables(_options);
        Name = name;
    }

    /// <inheritdoc />
    public ResourceName Name { get; }

    /// <inheritdoc />
    public string Artifact => "Assimalign.Cohesion.Database.Application";

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> EnvironmentVariables => _environmentVariables;

    /// <inheritdoc />
    public IReadOnlyList<ResourceEndpoint> Endpoints => new[]
    {
        new ResourceEndpoint(EndpointName, EndpointScheme, _options.Port, IsPublic: false),
    };

    /// <inheritdoc />
    public IReadOnlyList<ResourceMount> Mounts => new[]
    {
        new ResourceMount("data", _options.DataMountPath, ResourceMountKind.Volume),
    };

    /// <summary>
    /// Merges the caller's environment variables with the conventional database
    /// variables the host binds. The data path always maps to the mount path; the port
    /// is set only when explicitly declared (a platform-allocated port is injected by
    /// the gateway at launch); durability is set only when configured. Caller-set
    /// values win on a name clash.
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildEnvironmentVariables(DatabaseResourceOptions options)
    {
        var variables = new Dictionary<string, string>(options.EnvironmentVariables, StringComparer.Ordinal);

        variables.TryAdd(DataPathVariable, options.DataMountPath);

        if (options.Port > 0)
        {
            variables.TryAdd(PortVariable, options.Port.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(options.Durability))
        {
            variables.TryAdd(DurabilityVariable, options.Durability);
        }

        return variables;
    }
}
