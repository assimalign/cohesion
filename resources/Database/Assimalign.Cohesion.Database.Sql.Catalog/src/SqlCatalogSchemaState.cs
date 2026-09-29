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
    public SqlCatalogSchemaState(string contentHash, string canonicalDocument)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDocument);

        ContentHash = contentHash;
        CanonicalDocument = canonicalDocument;
    }

    /// <summary>
    /// Gets the deterministic content hash of the compiled schema.
    /// </summary>
    public string ContentHash { get; }

    /// <summary>
    /// Gets the canonical compiled-schema document.
    /// </summary>
    public string CanonicalDocument { get; }
}
