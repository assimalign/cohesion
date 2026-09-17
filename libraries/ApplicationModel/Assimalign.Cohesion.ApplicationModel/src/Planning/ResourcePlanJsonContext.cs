using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Provides reflection-free System.Text.Json metadata for the realization-plan wire contract.
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Default,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ResourcePlan))]
public sealed partial class ResourcePlanJsonContext : JsonSerializerContext;
