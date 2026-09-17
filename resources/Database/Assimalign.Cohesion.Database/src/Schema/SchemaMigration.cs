using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database;

/// <summary>Identifies the semantic change performed by a migration step.</summary>
public enum SchemaMigrationOperationKind : byte
{
    /// <summary>Adds a table.</summary>
    AddTable = 0,
    /// <summary>Alters table key or constraint metadata.</summary>
    AlterTable,
    /// <summary>Drops a table.</summary>
    DropTable,
    /// <summary>Adds a column.</summary>
    AddColumn,
    /// <summary>Alters a column.</summary>
    AlterColumn,
    /// <summary>Drops a column.</summary>
    DropColumn,
    /// <summary>Adds an index.</summary>
    AddIndex,
    /// <summary>Drops an index.</summary>
    DropIndex,
    /// <summary>Adds a key-value collection.</summary>
    AddCollection,
    /// <summary>Alters a key-value collection.</summary>
    AlterCollection,
    /// <summary>Drops a key-value collection.</summary>
    DropCollection,
}

/// <summary>Classifies the data-loss risk of a migration step.</summary>
public enum SchemaMigrationSafety : byte
{
    /// <summary>The operation preserves existing logical data.</summary>
    Safe = 0,
    /// <summary>The operation can remove data or make existing values invalid.</summary>
    Destructive,
}

/// <summary>Describes one ordered migration operation.</summary>
public sealed class SchemaMigrationOperation
{
    /// <summary>Initializes a migration operation.</summary>
    /// <param name="kind">The operation kind.</param>
    /// <param name="safety">The safety classification.</param>
    /// <param name="objectName">The affected object name.</param>
    /// <param name="parentName">The owning table or collection name, when applicable.</param>
    /// <param name="table">The desired table definition, when applicable.</param>
    /// <param name="column">The desired column definition, when applicable.</param>
    /// <param name="previousColumn">The previous column definition, when applicable.</param>
    /// <param name="index">The desired index definition, when applicable.</param>
    public SchemaMigrationOperation(
        SchemaMigrationOperationKind kind,
        SchemaMigrationSafety safety,
        string objectName,
        string? parentName = null,
        CompiledSchemaTable? table = null,
        CompiledSchemaColumn? column = null,
        CompiledSchemaColumn? previousColumn = null,
        CompiledSchemaIndex? index = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectName);
        Kind = kind;
        Safety = safety;
        ObjectName = objectName;
        ParentName = parentName;
        Table = table;
        Column = column;
        PreviousColumn = previousColumn;
        Index = index;
    }

    /// <summary>Gets the operation kind.</summary>
    public SchemaMigrationOperationKind Kind { get; }
    /// <summary>Gets the safety classification.</summary>
    public SchemaMigrationSafety Safety { get; }
    /// <summary>Gets the affected object name.</summary>
    public string ObjectName { get; }
    /// <summary>Gets the owning table or collection name.</summary>
    public string? ParentName { get; }
    /// <summary>Gets the desired table definition.</summary>
    public CompiledSchemaTable? Table { get; }
    /// <summary>Gets the desired column definition.</summary>
    public CompiledSchemaColumn? Column { get; }
    /// <summary>Gets the previous column definition.</summary>
    public CompiledSchemaColumn? PreviousColumn { get; }
    /// <summary>Gets the desired index definition.</summary>
    public CompiledSchemaIndex? Index { get; }
}

/// <summary>Contains a deterministic ordered migration plan.</summary>
public sealed class SchemaMigrationPlan
{
    /// <summary>Initializes a migration plan.</summary>
    /// <param name="sourceHash">The source schema hash, or null for an empty database.</param>
    /// <param name="targetHash">The desired schema hash.</param>
    /// <param name="operations">The ordered operations.</param>
    public SchemaMigrationPlan(
        string? sourceHash,
        string targetHash,
        IReadOnlyList<SchemaMigrationOperation> operations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetHash);
        ArgumentNullException.ThrowIfNull(operations);
        SourceHash = sourceHash;
        TargetHash = targetHash;
        var copy = new SchemaMigrationOperation[operations.Count];
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
    public IReadOnlyList<SchemaMigrationOperation> Operations { get; }
    /// <summary>Gets a value indicating whether no migration work is required.</summary>
    public bool IsEmpty => Operations.Count == 0;
}

/// <summary>Describes the outcome of applying a compiled schema.</summary>
public sealed class SchemaMigrationResult
{
    /// <summary>Initializes a schema migration result.</summary>
    /// <param name="sourceHash">The source schema hash.</param>
    /// <param name="targetHash">The desired schema hash.</param>
    /// <param name="appliedOperationCount">The number of operations applied.</param>
    /// <param name="wasAlreadyApplied">Whether the desired hash was already recorded.</param>
    public SchemaMigrationResult(
        string? sourceHash,
        string targetHash,
        int appliedOperationCount,
        bool wasAlreadyApplied)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetHash);
        ArgumentOutOfRangeException.ThrowIfNegative(appliedOperationCount);
        SourceHash = sourceHash;
        TargetHash = targetHash;
        AppliedOperationCount = appliedOperationCount;
        WasAlreadyApplied = wasAlreadyApplied;
    }

    /// <summary>Gets the source schema hash.</summary>
    public string? SourceHash { get; }
    /// <summary>Gets the desired schema hash.</summary>
    public string TargetHash { get; }
    /// <summary>Gets the number of applied operations.</summary>
    public int AppliedOperationCount { get; }
    /// <summary>Gets a value indicating whether the desired schema was already applied.</summary>
    public bool WasAlreadyApplied { get; }
}

/// <summary>Applies a compiled schema to a logical database.</summary>
public interface IDatabaseSchemaProvisioner
{
    /// <summary>Diffs and applies the desired compiled schema.</summary>
    /// <param name="schema">The desired schema.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The migration result.</returns>
    ValueTask<SchemaMigrationResult> ApplySchemaAsync(
        CompiledSchema schema,
        CancellationToken cancellationToken = default);
}

/// <summary>Represents a migration planning or application failure.</summary>
public sealed class DatabaseSchemaMigrationException : DatabaseException
{
    /// <summary>Initializes a migration failure.</summary>
    /// <param name="message">The failure message.</param>
    public DatabaseSchemaMigrationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a migration failure with an underlying cause.</summary>
    /// <param name="message">The failure message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public DatabaseSchemaMigrationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
