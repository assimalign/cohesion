using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Ebml;

/// <summary>An immutable, ordinal DocType-to-schema registry for derived EBML formats.</summary>
[global::System.Diagnostics.DebuggerDisplay("EbmlSchemaRegistry")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class EbmlSchemaRegistry
{
    private readonly FrozenDictionary<string, EbmlSchema> _schemas;
    private EbmlSchemaRegistry(Dictionary<string, EbmlSchema> schemas) { _schemas = schemas.ToFrozenDictionary(StringComparer.Ordinal); }

    /// <summary>Creates a registry from hand-written schemas.</summary>
    /// <param name="schemas">DocType keys and composed schemas; input entries are copied.</param>
    /// <returns>The immutable registry.</returns>
    /// <exception cref="ArgumentNullException">The collection or a schema is null.</exception>
    /// <exception cref="ArgumentException">A key is empty or duplicated.</exception>
    public static EbmlSchemaRegistry Create(IEnumerable<KeyValuePair<string, EbmlSchema>> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        var copy = new Dictionary<string, EbmlSchema>(StringComparer.Ordinal);
        foreach (var pair in schemas)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pair.Key);
            ArgumentNullException.ThrowIfNull(pair.Value);
            if (!copy.TryAdd(pair.Key, pair.Value))
            {
                throw new ArgumentException(ErrorMessages.SchemaConflict, nameof(schemas));
            }
        }
        return new EbmlSchemaRegistry(copy);
    }

    /// <summary>Resolves the exact DocType, without case folding.</summary>
    /// <param name="docType">The DocType from the EBML header.</param>
    /// <returns>The schema, or null when unregistered.</returns>
    /// <exception cref="ArgumentNullException">The DocType is null.</exception>
    public EbmlSchema? Find(string docType)
    {
        ArgumentNullException.ThrowIfNull(docType);
        return _schemas.TryGetValue(docType, out var schema) ? schema : null;
    }

    private sealed class DebugView
    {
        public DebugView(EbmlSchemaRegistry value)
        {
        }
    }
}
