using System;
using System.Linq;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>The expected statement target and preceding state for one atomic script publication.</summary>
public sealed class SqlCatalogScriptPublication
{
    /// <summary>Creates an immutable expectation; the catalog validates it against the actual mutation under its metadata lock.</summary>
    /// <param name="name">The stable, case-sensitive script name.</param>
    /// <param name="contentHash">The SHA-256 of the exact UTF-8 script body.</param>
    /// <param name="statementCount">The parsed statement count.</param>
    /// <param name="previous">The exact preceding durable receipt, or null if the script has not begun.</param>
    /// <param name="expectedGeneration">The catalog generation observed before the mutation.</param>
    /// <param name="schemaHash">The typed schema hash at execution, or null.</param>
    /// <param name="expectedObject">The statement's expected target: T:"schema"."table", I:"schema"."table"."index", F:"name"(storage types) or D:"name"; empty for a non-DDL statement. Every identifier is double-quoted and embedded quotes are doubled. Names compare case-insensitively.</param>
    /// <exception cref="ArgumentException">A name, digest, statement count or target is invalid.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="expectedObject"/> is null.</exception>
    public SqlCatalogScriptPublication(
        string name,
        string contentHash,
        int statementCount,
        SqlCatalogScript? previous,
        ulong expectedGeneration,
        string? schemaHash,
        string expectedObject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        ArgumentNullException.ThrowIfNull(expectedObject);
        if (contentHash.Length != 64 || !contentHash.All(Uri.IsHexDigit) || statementCount <= 0 ||
            expectedObject.Length != 0 && string.IsNullOrWhiteSpace(expectedObject))
        {
            throw new ArgumentException("Invalid declared script publication expectation.");
        }
        Name = name;
        ContentHash = contentHash;
        StatementCount = statementCount;
        Previous = previous;
        ExpectedGeneration = expectedGeneration;
        SchemaHash = schemaHash;
        ExpectedObject = expectedObject;
    }

    /// <summary>Gets the stable, case-sensitive script name.</summary>
    public string Name { get; }
    /// <summary>Gets the SHA-256 of the exact script body.</summary>
    public string ContentHash { get; }
    /// <summary>Gets the parsed statement count.</summary>
    public int StatementCount { get; }
    /// <summary>Gets the exact expected preceding receipt, or null before the script starts.</summary>
    public SqlCatalogScript? Previous { get; }
    /// <summary>Gets the expected live catalog generation before publication.</summary>
    public ulong ExpectedGeneration { get; }
    /// <summary>Gets the typed schema hash before script execution.</summary>
    public string? SchemaHash { get; }
    /// <summary>Gets the expected mutation target; an empty string identifies a non-DDL statement.</summary>
    public string ExpectedObject { get; }
    /// <summary>Gets the prefix that successful publication completes.</summary>
    public int CompletedStatements => (Previous?.CompletedStatements ?? 0) + 1;
    /// <summary>Gets the catalog generation before this script began.</summary>
    public ulong BaseGeneration => Previous?.BaseGeneration ?? ExpectedGeneration;
}
