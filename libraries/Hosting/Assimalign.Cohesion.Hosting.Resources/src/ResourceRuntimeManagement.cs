using System.Text.Json.Serialization;

using Assimalign.Cohesion.Hosting.Resources.Internal;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>Describes a logical control-plane binding, independent of platform addresses.</summary>
public sealed record ResourceRuntimeManagement
{
    /// <summary>Gets the resource's declared control-plane endpoint name.</summary>
    [JsonConverter(typeof(ResourceManagementEndpointNameJsonConverter))]
    public ResourceManagementEndpointName Endpoint { get; init; }
    /// <summary>Gets the management path below that endpoint.</summary>
    [JsonConverter(typeof(ResourceManagementPathJsonConverter))]
    public ResourceManagementPath Path { get; init; } = "/cohesion/v1";
    /// <summary>Gets the versioned runtime state protocol.</summary>
    public string Protocol { get; init; } = ResourceRuntimeManifest.StateProtocol;
}
