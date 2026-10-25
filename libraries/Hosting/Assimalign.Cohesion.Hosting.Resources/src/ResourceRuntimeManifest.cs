using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using Assimalign.Cohesion.Hosting.Resources.Internal;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>Describes the runtime state surface exported by a resource producer.</summary>
public sealed record ResourceRuntimeManifest
{
    /// <summary>The supported portable runtime manifest schema.</summary>
    public const string CurrentSchema = "cohesion/resource-runtime/v1";
    /// <summary>The supported local state management protocol.</summary>
    public const string StateProtocol = "cohesion/resource-state/v1";
    /// <summary>Gets the versioned runtime manifest schema.</summary>
    public string Schema { get; init; } = CurrentSchema;
    /// <summary>Gets the logical management endpoint, never a live address.</summary>
    public ResourceRuntimeManagement Management { get; init; } = new();
    /// <summary>Gets state capabilities discovered from registered handlers at startup.</summary>
    public IReadOnlyList<ResourceStateDefinition> States { get; init; } = Array.Empty<ResourceStateDefinition>();
    /// <summary>Gets reproducible baselines captured from effective startup configuration.</summary>
    public IReadOnlyList<ResourceStateDeclaration> InitialState { get; init; } = Array.Empty<ResourceStateDeclaration>();

    /// <summary>Parses and validates a producer's runtime manifest.</summary>
    /// <param name="json">The JSON document.</param>
    /// <returns>The validated runtime manifest.</returns>
    /// <exception cref="ArgumentNullException">The document is null.</exception>
    /// <exception cref="JsonException">The document is malformed.</exception>
    /// <exception cref="InvalidDataException">The runtime contract is invalid or unsupported.</exception>
    public static ResourceRuntimeManifest Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        ResourceRuntimeManifest manifest = JsonSerializer.Deserialize(json, ResourceStateJsonContext.Default.ResourceRuntimeManifest)
            ?? throw new InvalidDataException(ErrorMessages.ResourceRuntimeInvalid);
        return manifest.Validate();
    }

    /// <summary>Exports the portable runtime manifest without live state or platform information.</summary>
    /// <returns>The UTF-8 JSON document.</returns>
    /// <exception cref="InvalidDataException">The runtime contract is invalid.</exception>
    public byte[] Export()
    {
        Validate();
        return JsonSerializer.SerializeToUtf8Bytes(this, ResourceStateJsonContext.Default.ResourceRuntimeManifest);
    }

    /// <summary>Validates the runtime envelope and catalog using resource-owned rules.</summary>
    /// <returns>This validated manifest.</returns>
    /// <exception cref="InvalidDataException">The envelope, catalog or initial state is invalid.</exception>
    public ResourceRuntimeManifest Validate()
    {
        if (Schema != CurrentSchema || Management is null || Management.Protocol != StateProtocol ||
            Management.Endpoint.Value is null || Management.Path.Value is null ||
            States is null || InitialState is null)
        {
            throw new InvalidDataException(ErrorMessages.ResourceRuntimeInvalid);
        }
        var names = new HashSet<ResourceStateName>();
        foreach (ResourceStateDefinition definition in States)
        {
            if (definition is null || definition.Name.Value is null || !names.Add(definition.Name) ||
                !Uri.TryCreate(definition.PayloadSchemaId, UriKind.Absolute, out _) ||
                string.IsNullOrWhiteSpace(definition.ContractDigest) ||
                !ResourceStateWire.IsSupportedSchema(definition.KeySchema) ||
                !ResourceStateWire.IsSupportedSchema(definition.PayloadSchema) ||
                !Enum.IsDefined(definition.Scope) || definition.Uses is not { Length: > 0 } ||
                definition.Uses.Any(static use => !Enum.IsDefined(use)) ||
                definition.Uses.Distinct().Count() != definition.Uses.Length)
            {
                throw new InvalidDataException(ErrorMessages.ResourceStateDefinitionInvalid);
            }
            JsonElement contract = JsonSerializer.SerializeToElement(definition, ResourceStateJsonContext.Default.ResourceStateDefinition);
            if (ResourceStateWire.ComputeDigest(contract, "contractDigest") != definition.ContractDigest)
            {
                throw new InvalidDataException(ErrorMessages.ResourceStateDefinitionInvalid);
            }
        }
        var slots = new HashSet<(ResourceStateName, ResourceStateKey)>();
        foreach (ResourceStateDeclaration declaration in InitialState)
        {
            if (declaration is null || !names.Contains(declaration.Name) || declaration.Key.Value is null ||
                !slots.Add((declaration.Name, declaration.Key)) || declaration.Desired is null ||
                declaration.Desired.Value.ValueKind == JsonValueKind.Undefined)
            {
                throw new InvalidDataException(ErrorMessages.ResourceStateDeclarationInvalid);
            }
            ResourceStateDefinition definition = States.First(state => state.Name == declaration.Name);
            if (!definition.Uses.Contains(ResourceStateUse.Orchestration) ||
                (!declaration.Desired.Exists && !definition.SupportsAbsence) ||
                !ResourceStateWire.ValidateValue(definition.KeySchema, JsonSerializer.SerializeToElement(declaration.Key.Value, ResourceStateJsonContext.Default.String)) ||
                (declaration.Desired.Exists && !ResourceStateWire.ValidateValue(definition.PayloadSchema, declaration.Desired.Value)))
            {
                throw new InvalidDataException(ErrorMessages.ResourceStateDeclarationInvalid);
            }
        }
        return this;
    }
}
