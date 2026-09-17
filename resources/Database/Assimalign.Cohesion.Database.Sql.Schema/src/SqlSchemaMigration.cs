using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>Identifies the semantic change performed by a migration step.</summary>
public enum SqlSchemaMigrationOperationKind : byte
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
    /// <summary>Adds a foreign-key or check constraint.</summary>
    AddConstraint,
    /// <summary>Drops a foreign-key or check constraint.</summary>
    DropConstraint,
}

/// <summary>Classifies the data-loss risk of a migration step.</summary>
public enum SqlSchemaMigrationSafety : byte
{
    /// <summary>The operation preserves existing logical data.</summary>
    Safe = 0,
    /// <summary>The operation can remove data or make existing values invalid.</summary>
    Destructive,
}

/// <summary>Describes one ordered migration operation.</summary>
public sealed class SqlSchemaMigrationOperation
{
    /// <summary>Initializes a migration operation.</summary>
    /// <param name="kind">The operation kind.</param>
    /// <param name="safety">The safety classification.</param>
    /// <param name="objectName">The affected object name.</param>
    /// <param name="parentName">The owning table name, when applicable.</param>
    /// <param name="table">The desired table definition, when applicable.</param>
    /// <param name="column">The desired column definition, when applicable.</param>
    /// <param name="previousColumn">The previous column definition, when applicable.</param>
    /// <param name="index">The desired index definition, when applicable.</param>
    /// <param name="constraint">The constraint definition, when applicable.</param>
    public SqlSchemaMigrationOperation(
        SqlSchemaMigrationOperationKind kind,
        SqlSchemaMigrationSafety safety,
        string objectName,
        string? parentName = null,
        CompiledSchemaTable? table = null,
        CompiledSchemaColumn? column = null,
        CompiledSchemaColumn? previousColumn = null,
        CompiledSchemaIndex? index = null,
        CompiledSchemaConstraint? constraint = null)
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
        Constraint = constraint;
    }

    /// <summary>Gets the operation kind.</summary>
    public SqlSchemaMigrationOperationKind Kind { get; }
    /// <summary>Gets the safety classification.</summary>
    public SqlSchemaMigrationSafety Safety { get; }
    /// <summary>Gets the affected object name.</summary>
    public string ObjectName { get; }
    /// <summary>Gets the owning table name.</summary>
    public string? ParentName { get; }
    /// <summary>Gets the desired table definition.</summary>
    public CompiledSchemaTable? Table { get; }
    /// <summary>Gets the desired column definition.</summary>
    public CompiledSchemaColumn? Column { get; }
    /// <summary>Gets the previous column definition.</summary>
    public CompiledSchemaColumn? PreviousColumn { get; }
    /// <summary>Gets the desired index definition.</summary>
    public CompiledSchemaIndex? Index { get; }
    /// <summary>Gets the constraint definition.</summary>
    public CompiledSchemaConstraint? Constraint { get; }
}

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

/// <summary>Represents a migration planning or application failure.</summary>
public sealed class SqlSchemaMigrationException : DatabaseException
{
    /// <summary>Initializes a migration failure.</summary>
    /// <param name="message">The failure message.</param>
    public SqlSchemaMigrationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a migration failure with an underlying cause.</summary>
    /// <param name="message">The failure message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public SqlSchemaMigrationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
