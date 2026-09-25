using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane.Internal;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Default,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ControlPlaneResourceDocument))]
[JsonSerializable(typeof(ControlPlaneCommandRequest))]
[JsonSerializable(typeof(ControlPlaneCommandObservation[]))]
[JsonSerializable(typeof(ControlPlaneMetadataDocument))]
[JsonSerializable(typeof(ControlPlaneErrorDocument))]
internal sealed partial class ControlPlaneJsonContext : JsonSerializerContext;

internal sealed class ControlPlaneResourceDocument
{
    public required string Name { get; init; }

    public required string Kind { get; init; }

    public required string State { get; init; }

    public IReadOnlyList<ControlPlaneEndpointDocument> Endpoints { get; init; } =
        Array.Empty<ControlPlaneEndpointDocument>();
}

internal sealed class ControlPlaneEndpointDocument
{
    public required string Name { get; init; }

    public required string Address { get; init; }

    public bool IsPublic { get; init; }
}

internal sealed class ControlPlaneCommandRequest
{
    public string? Id { get; init; }

    public string? Kind { get; init; }

    public string? Owner { get; init; }

    public string? Key { get; init; }

    public byte[]? Payload { get; init; }
}

internal sealed class ControlPlaneCommandObservation
{
    public required string Id { get; init; }

    public required string Kind { get; init; }

    public required string Owner { get; init; }

    public required string Key { get; init; }

    public required byte[] Payload { get; init; }

    public required string Status { get; init; }

    public required string Detail { get; init; }

    public byte[]? Result { get; init; }
}

internal sealed class ControlPlaneMetadataDocument
{
    public required string Url { get; init; }

    public required JsonElement TrustKey { get; init; }
}

internal sealed class ControlPlaneErrorDocument
{
    public required string Error { get; init; }
}
