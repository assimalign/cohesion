using System;
using System.Collections.Generic;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.Database.ApplicationModel;

/// <summary>
/// The orchestration manifest for a Cohesion database: an executable resource that
/// exposes the database wire-protocol endpoint and mounts a persistent data volume.
/// </summary>
/// <remarks>
/// This type describes the database to a gateway; it carries no runtime behavior.
/// The artifact name resolves to the standalone database host executable on the
/// local gateway and to its pre-built container image on container gateways.
/// </remarks>
public sealed class DatabaseResource : IApplicationResource, IExecutableResource, IEndpointResource, IMountResource
{
    private readonly DatabaseResourceOptions _options;

    /// <summary>
    /// The logical name of the wire-protocol endpoint declared by this resource.
    /// </summary>
    public const string EndpointName = "db";

    /// <summary>
    /// The scheme of the wire-protocol endpoint declared by this resource.
    /// </summary>
    public const string EndpointScheme = "cohesion-db";

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
        Name = name;
    }

    /// <inheritdoc />
    public ResourceName Name { get; }

    /// <inheritdoc />
    public string Artifact => "Assimalign.Cohesion.Database.Application";

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> EnvironmentVariables => _options.EnvironmentVariables;

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
}
