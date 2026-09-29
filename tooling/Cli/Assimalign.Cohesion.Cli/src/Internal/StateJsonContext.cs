using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Cli.Internal;

// Deliberately ignore unknown fields (especially model/commands/trustKey).
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ExportDocument))]
[JsonSerializable(typeof(PortDocument))]
[JsonSerializable(typeof(ProcessDocument))]
[JsonSerializable(typeof(ControlPlaneDocument))]
[JsonSerializable(typeof(ObservedResource))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class StateJsonContext : JsonSerializerContext;

internal sealed class ExportDocument
{
    public string Application { get; set; } = "";
    public string Environment { get; set; } = "";
    public string Version { get; set; } = "";
    public ExportResource[] Resources { get; set; } = [];
}

internal sealed class ExportResource
{
    public required string Name { get; set; }
    public string Kind { get; set; } = "";
    public string ManifestHash { get; set; } = "";
    public ExportEndpoint[] Endpoints { get; set; } = [];
}

internal sealed class ExportEndpoint
{
    public string Name { get; set; } = "";
    public string? Internal { get; set; }
    public string? Public { get; set; }
}

internal sealed class PortDocument
{
    public int? ControlPlane { get; set; }
    public Dictionary<string, Dictionary<string, int>> Resources { get; set; } = [];
}

internal sealed class ProcessDocument
{
    public int ProcessId { get; set; }
    public long StartTimeUtcTicks { get; set; }
}

internal sealed class ControlPlaneDocument
{
    public required string Url { get; set; }
}

internal sealed class ObservedResource
{
    public required string State { get; set; }
    public ObservedEndpoint[] Endpoints { get; set; } = [];
}

internal sealed class ObservedEndpoint
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public bool IsPublic { get; set; }
}
