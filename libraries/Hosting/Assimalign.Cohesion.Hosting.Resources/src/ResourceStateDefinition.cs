using System;
using System.Text.Json;
using System.Text.Json.Serialization;

using Assimalign.Cohesion.Hosting.Resources.Internal;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>Describes one resource-owned runtime state contract without live platform metadata.</summary>
public sealed record ResourceStateDefinition
{
    /// <summary>Gets the stable state name.</summary>
    [JsonConverter(typeof(ResourceStateNameJsonConverter))]
    public ResourceStateName Name { get; init; }
    /// <summary>Gets the versioned payload schema identity.</summary>
    public string PayloadSchemaId { get; init; } = string.Empty;
    /// <summary>Gets the digest of the canonical contract.</summary>
    public string ContractDigest { get; init; } = string.Empty;
    /// <summary>Gets the JSON schema for state slot keys.</summary>
    public JsonElement KeySchema { get; init; }
    /// <summary>Gets the JSON schema for desired values.</summary>
    public JsonElement PayloadSchema { get; init; }
    /// <summary>Gets the resource-defined distribution semantic.</summary>
    public ResourceStateScope Scope { get; init; }
    /// <summary>Gets the permitted invocation stages.</summary>
    public ResourceStateUse[] Uses { get; init; } = [];
    /// <summary>Gets whether an explicit absent value is supported.</summary>
    public bool SupportsAbsence { get; init; }

    /// <summary>Creates a detached definition and computes its canonical contract digest.</summary>
    /// <param name="name">The validated state name.</param>
    /// <param name="payloadSchemaId">The absolute schema identity.</param>
    /// <param name="keySchema">The supported slot key schema.</param>
    /// <param name="payloadSchema">The desired value schema.</param>
    /// <param name="scope">The resource-defined delivery semantic.</param>
    /// <param name="uses">The permitted invocation stages.</param>
    /// <param name="supportsAbsence">Whether explicit absence is supported.</param>
    /// <returns>A detached state definition with its digest.</returns>
    /// <exception cref="ArgumentNullException">The schema identity or uses are null.</exception>
    /// <exception cref="ArgumentException">The name, schema identity or schemas are invalid.</exception>
    public static ResourceStateDefinition Create(ResourceStateName name, Uri payloadSchemaId,
        JsonElement keySchema, JsonElement payloadSchema, ResourceStateScope scope,
        ResourceStateUse[] uses, bool supportsAbsence = false)
    {
        ArgumentNullException.ThrowIfNull(payloadSchemaId);
        ArgumentNullException.ThrowIfNull(uses);
        if (name.Value is null || !payloadSchemaId.IsAbsoluteUri || keySchema.ValueKind != JsonValueKind.Object || payloadSchema.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(ErrorMessages.ResourceStateDefinitionInvalid);
        }
        var definition = new ResourceStateDefinition
        {
            Name = name, PayloadSchemaId = payloadSchemaId.AbsoluteUri, KeySchema = keySchema.Clone(),
            PayloadSchema = payloadSchema.Clone(), Scope = scope, Uses = (ResourceStateUse[])uses.Clone(), SupportsAbsence = supportsAbsence
        };
        JsonElement document = JsonSerializer.SerializeToElement(definition, ResourceStateJsonContext.Default.ResourceStateDefinition);
        return definition with { ContractDigest = ResourceStateWire.ComputeDigest(document, "contractDigest") };
    }
}

/// <summary>Defines how a gateway reconciles resource state.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ResourceStateScope>))]
public enum ResourceStateScope
{
    /// <summary>Every current and future process must apply the value.</summary>
    [JsonStringEnumMemberName("everyInstance")]
    EveryInstance,
    /// <summary>One fenced executor reconciles state in a shared backend.</summary>
    [JsonStringEnumMemberName("sharedState")]
    SharedState
}

/// <summary>Identifies when a resource permits a state mutation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ResourceStateUse>))]
public enum ResourceStateUse
{
    /// <summary>The gateway may apply declared state during orchestration.</summary>
    [JsonStringEnumMemberName("orchestration")]
    Orchestration,
    /// <summary>An authorized management caller may replace desired state.</summary>
    [JsonStringEnumMemberName("management")]
    Management
}
