using System;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>An immutable, forward-only declared-script receipt read from durable catalog state.</summary>
public sealed record SqlCatalogScript
{
    /// <summary>Creates a validated script receipt.</summary>
    /// <param name="name">The stable script name.</param>
    /// <param name="contentHash">The SHA-256 of the exact UTF-8 script body.</param>
    /// <param name="statementCount">The parsed statement count.</param>
    /// <param name="completedStatements">The durably completed statement prefix.</param>
    /// <param name="baseGeneration">The catalog generation before this script began.</param>
    /// <param name="generation">The last catalog generation committed by the script.</param>
    /// <param name="schemaHash">The typed schema hash at execution, or null.</param>
    /// <param name="objects">Table and index identities introduced by this script.</param>
    /// <param name="statementObjects">One catalog-derived target per completed statement, or an empty string for a non-DDL statement.</param>
    /// <param name="statementParameterTypes">One parameter-type vector per completed statement: CREATE FUNCTION retains the definition's types, DROP FUNCTION retains its requested types resolved by SQL to the removed signature, and other statements have an empty vector. Null supplies empty vectors.</param>
    /// <exception cref="ArgumentException">An identity, digest or prefix is invalid.</exception>
    public SqlCatalogScript(
        string name,
        string contentHash,
        int statementCount,
        int completedStatements,
        ulong baseGeneration,
        ulong generation,
        string? schemaHash,
        IReadOnlyList<string>? objects = null,
        IReadOnlyList<string>? statementObjects = null,
        IReadOnlyList<IReadOnlyList<string>>? statementParameterTypes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        if (contentHash.Length != 64 || !System.Linq.Enumerable.All(contentHash, Uri.IsHexDigit) ||
            statementCount < 0 || completedStatements < 0 || completedStatements > statementCount || generation < baseGeneration)
        {
            throw new ArgumentException(ErrorMessages.SqlCatalogScriptArgument);
        }
        Name = name;
        ContentHash = contentHash;
        StatementCount = statementCount;
        CompletedStatements = completedStatements;
        BaseGeneration = baseGeneration;
        Generation = generation;
        SchemaHash = schemaHash;
        Objects = Array.AsReadOnly(objects?.ToArray() ?? []);
        StatementObjects = Array.AsReadOnly(statementObjects?.ToArray() ?? []);
        if (StatementObjects.Count != completedStatements || StatementObjects.Any(value => value is null) ||
            Objects.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(ErrorMessages.SqlCatalogScriptArgument2);
        }
        if (statementParameterTypes is not null && (statementParameterTypes.Count != completedStatements ||
            statementParameterTypes.Any(types => types is null || types.Any(string.IsNullOrWhiteSpace))))
        {
            throw new ArgumentException(ErrorMessages.SqlCatalogScriptArgument3);
        }
        StatementParameterTypes = Array.AsReadOnly(statementParameterTypes is null
            ? Enumerable.Range(0, completedStatements).Select(_ => (IReadOnlyList<string>)Array.Empty<string>()).ToArray()
            : statementParameterTypes.Select(types => (IReadOnlyList<string>)Array.AsReadOnly(types.ToArray())).ToArray());
    }
    /// <summary>Gets the script-owned table and index identities.</summary>
    public IReadOnlyList<string> Objects { get; }
    /// <summary>Gets the ordered catalog-derived target of each completed statement; non-DDL statements have an empty target.</summary>
    public IReadOnlyList<string> StatementObjects { get; }
    /// <summary>Gets the created definition's parameter types or the drop's requested types resolved by SQL; other statements have an empty vector.</summary>
    public IReadOnlyList<IReadOnlyList<string>> StatementParameterTypes { get; }
    /// <summary>Gets the stable, case-sensitive name.</summary>
    public string Name { get; }
    /// <summary>Gets the SHA-256 of the exact script body.</summary>
    public string ContentHash { get; }
    /// <summary>Gets the statement count.</summary>
    public int StatementCount { get; }
    /// <summary>Gets the durably committed statement prefix.</summary>
    public int CompletedStatements { get; }
    /// <summary>Gets the generation before the script began.</summary>
    public ulong BaseGeneration { get; }
    /// <summary>Gets the last generation committed by the script.</summary>
    public ulong Generation { get; }
    /// <summary>Gets the typed schema hash before script execution.</summary>
    public string? SchemaHash { get; }
}
