using System;
using System.Collections.Generic;
using System.Linq;
namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>A durable, forward-only declared-script receipt. Statement positions are one-based completed prefixes.</summary>
public sealed record SqlCatalogScript
{
    /// <summary>Creates a validated script receipt.</summary>
    /// <param name="name">The stable script name.</param>
    /// <param name="contentHash">The SHA-256 of the exact UTF-8 script body.</param>
    /// <param name="statementCount">The parsed statement count.</param>
    /// <param name="completedStatements">The durably committed prefix.</param>
    /// <param name="baseGeneration">The catalog generation before this script began.</param>
    /// <param name="generation">The last catalog generation this script committed.</param>
    /// <param name="schemaHash">The typed schema hash at execution, or null.</param>
    /// <param name="objects">Table and index identities introduced by this script.</param>
    /// <exception cref="ArgumentException">An identity, digest or prefix is invalid.</exception>
    public SqlCatalogScript(string name, string contentHash, int statementCount, int completedStatements,
        ulong baseGeneration, ulong generation, string? schemaHash, IReadOnlyList<string>? objects = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        if (contentHash.Length != 64 || !System.Linq.Enumerable.All(contentHash, Uri.IsHexDigit) ||
            statementCount < 0 || completedStatements < 0 || completedStatements > statementCount || generation < baseGeneration)
        {
            throw new ArgumentException("Invalid declared script receipt.");
        }
        Name = name;
        ContentHash = contentHash;
        StatementCount = statementCount;
        CompletedStatements = completedStatements;
        BaseGeneration = baseGeneration;
        Generation = generation;
        SchemaHash = schemaHash;
        Objects = Array.AsReadOnly(objects?.ToArray() ?? []);
    }
    /// <summary>Gets the script-owned table and index identities.</summary>
    public IReadOnlyList<string> Objects { get; }
    /// <summary>Gets the stable, case-sensitive name.</summary>
    public string Name { get; }
    /// <summary>Gets the SHA-256 of the exact script body.</summary>
    public string ContentHash { get; }
    /// <summary>Gets the statement count.</summary>
    public int StatementCount { get; }
    /// <summary>Gets the committed statement prefix.</summary>
    public int CompletedStatements { get; }
    /// <summary>Gets the generation before the script began.</summary>
    public ulong BaseGeneration { get; }
    /// <summary>Gets the last generation committed by the script.</summary>
    public ulong Generation { get; }
    /// <summary>Gets the typed schema hash before script execution.</summary>
    public string? SchemaHash { get; }
}

/// <summary>The constant-time reconciliation marker for an ordered declared-script set.</summary>
/// <param name="ContentHash">The SHA-256 of the ordered names and body digests.</param>
/// <param name="Generation">The catalog generation at successful reconciliation.</param>
public sealed record SqlCatalogScriptState(string ContentHash, ulong Generation);
