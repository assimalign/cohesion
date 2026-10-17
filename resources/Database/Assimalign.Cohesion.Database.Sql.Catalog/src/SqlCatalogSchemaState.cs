using System;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>
/// Identifies the canonical compiled schema most recently applied to a SQL database.
/// </summary>
public sealed class SqlCatalogSchemaState
{
    /// <summary>
    /// Initializes a new applied-schema state.
    /// </summary>
    /// <param name="contentHash">The deterministic content hash of the compiled schema.</param>
    /// <param name="canonicalDocument">The canonical compiled-schema document.</param>
    /// <param name="generation">The catalog DDL generation after every schema step succeeded.</param>
    /// <exception cref="ArgumentException">The content hash or canonical document is blank.</exception>
    public SqlCatalogSchemaState(string contentHash, string canonicalDocument, ulong generation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDocument);

        ContentHash = contentHash;
        CanonicalDocument = canonicalDocument;
        Generation = generation;
    }

    /// <summary>
    /// Gets the deterministic content hash of the compiled schema.
    /// </summary>
    public string ContentHash { get; }

    /// <summary>
    /// Gets the canonical compiled-schema document.
    /// </summary>
    public string CanonicalDocument { get; }

    /// <summary>
    /// Gets the catalog DDL generation at which this schema was fully applied.
    /// </summary>
    public ulong Generation { get; }
}
