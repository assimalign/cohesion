using System;

namespace Assimalign.Cohesion.Database.Sql.Schema;

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
    /// <param name="domain">The domain definition, when applicable.</param>
    public SqlSchemaMigrationOperation(
        SqlSchemaMigrationOperationKind kind,
        SqlSchemaMigrationSafety safety,
        string objectName,
        string? parentName = null,
        CompiledSchemaTable? table = null,
        CompiledSchemaColumn? column = null,
        CompiledSchemaColumn? previousColumn = null,
        CompiledSchemaIndex? index = null,
        CompiledSchemaConstraint? constraint = null,
        CompiledSchemaType? domain = null)
    {
        Domain = domain;
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
    /// <summary>Gets the domain definition of an <see cref="SqlSchemaMigrationOperationKind.AddDomain"/> or <see cref="SqlSchemaMigrationOperationKind.DropDomain"/> step.</summary>
    public CompiledSchemaType? Domain { get; }
}
