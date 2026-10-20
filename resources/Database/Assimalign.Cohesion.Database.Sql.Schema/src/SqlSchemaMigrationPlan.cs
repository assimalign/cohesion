using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>Contains a deterministic ordered migration plan.</summary>
public sealed class SqlSchemaMigrationPlan
{
    /// <summary>Initializes a migration plan.</summary>
    /// <param name="sourceHash">The source schema hash, or null for an empty database.</param>
    /// <param name="targetHash">The desired schema hash.</param>
    /// <param name="operations">The ordered operations.</param>
    public SqlSchemaMigrationPlan(
        string? sourceHash,
        string targetHash,
        IReadOnlyList<SqlSchemaMigrationOperation> operations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetHash);
        ArgumentNullException.ThrowIfNull(operations);
        SourceHash = sourceHash;
        TargetHash = targetHash;
        var copy = new SqlSchemaMigrationOperation[operations.Count];
        for (int index = 0; index < operations.Count; index++)
        {
            copy[index] = operations[index];
        }

        Operations = Array.AsReadOnly(copy);
    }

    /// <summary>Gets the source schema hash.</summary>
    public string? SourceHash { get; }
    /// <summary>Gets the target schema hash.</summary>
    public string TargetHash { get; }
    /// <summary>Gets the ordered operations.</summary>
    public IReadOnlyList<SqlSchemaMigrationOperation> Operations { get; }
    /// <summary>Gets a value indicating whether no migration work is required.</summary>
    public bool IsEmpty => Operations.Count == 0;
}
