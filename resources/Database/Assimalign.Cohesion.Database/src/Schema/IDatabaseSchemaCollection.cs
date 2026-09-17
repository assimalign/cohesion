using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database;

/// <summary>Describes a key-value collection declaration.</summary>
public interface IDatabaseSchemaCollection
{
    /// <summary>Gets the stable collection name.</summary>
    string Name { get; }

    /// <summary>Gets the CLR entry type.</summary>
    Type EntryType { get; }

    /// <summary>Gets the typed field declarations.</summary>
    IReadOnlyList<IDatabaseSchemaColumn> Fields { get; }

    /// <summary>Gets the key field name.</summary>
    string? Key { get; }

    /// <summary>Gets the indexed field names.</summary>
    IReadOnlyList<string> Indexes { get; }

    /// <summary>
    /// Gets relational references written through the shared member builder. The compiler
    /// reports these as typed model-mismatch diagnostics instead of silently discarding them.
    /// </summary>
    IReadOnlyList<IDatabaseSchemaReference> References { get; }
}
