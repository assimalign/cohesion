using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Sdk.Tasks;

internal sealed class ResourceManifestModel
{
    public required string Name { get; init; }

    public required string Kind { get; init; }

    public required string Application { get; init; }

    public required string ApplicationModel { get; init; }

    public required ResourceArtifactModel Artifact { get; init; }

    public List<ResourceEndpointModel> Endpoints { get; } = [];

    public Dictionary<string, ResourceProbeModel> Probes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public required ResourceControlPlaneModel ControlPlane { get; init; }

    public List<ResourceMountModel> Mounts { get; } = [];

    public List<ResourceSettingModel> Settings { get; } = [];

    public List<string> Commands { get; } = [];

    public List<ResourceReferenceModel> References { get; } = [];

    public required ResourceLifecycleModel Lifecycle { get; init; }

    public Dictionary<string, string> Properties { get; } = new(StringComparer.Ordinal);
}

internal sealed record ResourceArtifactModel(
    string Assembly,
    bool Composable,
    string Project,
    string AppHost);

internal sealed record ResourceEndpointModel(
    string Name,
    string Scheme,
    string Protocol,
    int ContainerPort,
    int? DevPort,
    bool Public,
    string? Certificate);

internal sealed record ResourceProbeModel(
    string Endpoint,
    string? Http,
    bool? Tcp,
    IReadOnlyList<string>? Exec,
    string? Grpc,
    bool? None);

internal sealed record ResourceControlPlaneModel(string Endpoint, string Path);

internal sealed record ResourceMountModel(
    string Name,
    string Kind,
    string ContainerPath,
    string? Source,
    string? Size);

internal sealed record ResourceSettingModel(string Key, string? Default, string Type);

internal sealed class ResourceReferenceModel
{
    public required string Resource { get; init; }

    public required string Application { get; init; }

    public required bool Optional { get; init; }

    public required string Manifest { get; init; }

    public required ResourceArtifactModel Artifact { get; init; }

    public List<ResourceEndpointModel> Endpoints { get; } = [];

    public List<ResourceMountModel> Mounts { get; } = [];

    public required ResourceLifecycleModel Lifecycle { get; init; }
}

internal sealed record ResourceLifecycleModel(
    string Workload,
    int Replicas,
    int? MaxReplicas,
    int StopGraceSeconds,
    string RestartPolicy);
