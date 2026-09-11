using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Sdk.Gateway.Tasks;

internal sealed class GatewayManifest
{
    public required string Path { get; init; }

    public required string Name { get; init; }

    public required string Application { get; init; }

    public required string Kind { get; init; }

    public required string ApplicationModel { get; init; }

    public required string RawJson { get; init; }

    public required bool Composable { get; init; }

    public required string ControlPlaneEndpoint { get; init; }

    public required string ControlPlanePath { get; init; }

    public required string ProjectPath { get; init; }

    public required string ProjectName { get; init; }

    public required string RootNamespace { get; init; }

    public required string TargetPath { get; init; }

    public required string AppHostPath { get; init; }

    public required string ReferenceIdentity { get; init; }

    public required bool IsDirect { get; set; }

    public required bool IsDirectProjectReference { get; set; }

    public GatewayInProcessBinding? InProcessBinding { get; set; }

    public List<GatewayManifestEndpoint> Endpoints { get; } = [];

    public List<GatewayManifestMount> Mounts { get; } = [];

    public List<string> Commands { get; } = [];

    public List<GatewayManifestReference> References { get; } = [];

    public string MemberName { get; set; } = string.Empty;
}

internal sealed record GatewayInProcessBinding(
    string RootNamespace,
    string EntryPointType,
    string EntryAssemblyName,
    string ResourceName,
    string TargetPath,
    string ProjectPath);

internal sealed record GatewayManifestEndpoint(string Name);

internal sealed record GatewayManifestMount(string Name, string Kind, string? Source);

internal sealed record GatewayManifestReference(
    string Resource,
    string Application,
    IReadOnlyList<string> Endpoints,
    bool Optional);

internal sealed record GatewayResourceKind(
    string Kind,
    string ApplicationModel,
    string OptionsType,
    string DescriptorType,
    string AddMethod);

internal sealed record GatewayProvider(
    string MemberName,
    string Name,
    string GatewayType,
    string OptionsType,
    string? CommandLineApplyMethod,
    bool RequiresJit);

internal sealed record GatewayClientKind(string Kind, string PackageId);

internal sealed class GatewayExternal
{
    public required string MemberName { get; init; }

    public required string Resource { get; init; }

    public required string Application { get; init; }

    public required bool Optional { get; set; }

    public GatewayManifest? Manifest { get; init; }

    public HashSet<string> Endpoints { get; } = new(StringComparer.Ordinal);

    public List<GatewayManifest> Closure { get; } = [];
}
