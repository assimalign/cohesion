using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Ebml;

/// <summary>Composes immutable, hand-written EBML definitions without reflection or XML.</summary>
public sealed class EbmlSchemaBuilder
{
    private readonly Dictionary<ulong, EbmlElementDefinition> _definitions = new();

    /// <summary>Creates an empty builder; use Include to compose the RFC header schema.</summary>
    public EbmlSchemaBuilder() { }

    /// <summary>Adds an element definition.</summary>
    /// <param name="definition">The immutable definition.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException">The definition is null.</exception>
    /// <exception cref="ArgumentException">The encoded ID is already registered.</exception>
    public EbmlSchemaBuilder Add(EbmlElementDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!_definitions.TryAdd(definition.Identifier.EncodedValue, definition))
        {
            throw new ArgumentException(ErrorMessages.SchemaConflict, nameof(definition));
        }

        return this;
    }

    /// <summary>Includes all definitions from an existing schema.</summary>
    /// <param name="schema">The schema to compose.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException">The schema is null.</exception>
    /// <exception cref="ArgumentException">An ID is already registered.</exception>
    public EbmlSchemaBuilder Include(EbmlSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        foreach (var definition in schema.Definitions.Values)
        {
            Add(definition);
        }

        return this;
    }

    /// <summary>Builds an independent immutable snapshot.</summary>
    /// <returns>The schema.</returns>
    /// <exception cref="ArgumentException">A parent is absent or is not a master.</exception>
    public EbmlSchema Build()
    {
        foreach (var definition in _definitions.Values)
        {
            foreach (var parentId in definition.ParentIds)
            {
                if (!_definitions.TryGetValue(parentId.EncodedValue, out var parent) || parent.Type != EbmlElementType.MasterElement ||
                    (definition.UnknownSizeAllowed && !parent.UnknownSizeAllowed) ||
                    (parentId.EncodedValue == definition.Identifier.EncodedValue && !definition.Recursive))
                {
                    throw new ArgumentException(ErrorMessages.SchemaConflict);
                }
            }
        }

        return new EbmlSchema(_definitions);
    }
}
