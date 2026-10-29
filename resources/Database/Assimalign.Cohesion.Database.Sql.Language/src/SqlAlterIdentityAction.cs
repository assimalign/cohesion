using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>One ALTER COLUMN identity operation.</summary>
public sealed class SqlAlterIdentityAction : SqlAlterAction
{
    internal SqlAlterIdentityAction(string columnName, string operation, SqlIdentityDefinition? identity = null,
        bool? isAlways = null, IReadOnlyList<SqlSequenceOption>? options = null, bool ifExists = false)
    { ColumnName = columnName; Operation = operation; Identity = identity; IsAlways = isAlways; Options = options ?? []; IfExists = ifExists; }
    /// <summary>Gets the target column.</summary>
    public string ColumnName { get; }
    /// <summary>Gets ADD, DROP, SET GENERATED or OPTIONS.</summary>
    public string Operation { get; }
    /// <summary>Gets the declaration for ADD GENERATED.</summary>
    public SqlIdentityDefinition? Identity { get; }
    /// <summary>Gets the new generation mode for SET GENERATED.</summary>
    public bool? IsAlways { get; }
    /// <summary>Gets the explicit sequence changes.</summary>
    public IReadOnlyList<SqlSequenceOption> Options { get; }
    /// <summary>Gets whether a missing identity is ignored.</summary>
    public bool IfExists { get; }
}
