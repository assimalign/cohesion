using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Identifies a parsed table or column constraint.</summary>
public enum SqlConstraintKind
{
    /// <summary>A primary key.</summary>
    PrimaryKey,
    /// <summary>A reference to another table's key.</summary>
    ForeignKey,
    /// <summary>A row predicate.</summary>
    Check,
    /// <summary>A unique key, lowered to a unique catalog index.</summary>
    Unique,
}

/// <summary>Identifies the supported action when a referenced parent row is deleted.</summary>
public enum SqlReferentialAction
{
    /// <summary>Refuse deletion while children reference the parent.</summary>
    Restrict,
    /// <summary>Delete referencing children with their parent.</summary>
    Cascade,
}

/// <summary>Describes a parsed constraint, normalized for column and table declarations.</summary>
public sealed class SqlConstraintDefinition
{
    internal SqlConstraintDefinition(
        string? name,
        SqlConstraintKind kind,
        IReadOnlyList<string> columns,
        SqlTableReference? referencedTable = null,
        IReadOnlyList<string>? referencedColumns = null,
        SqlReferentialAction onDelete = SqlReferentialAction.Restrict,
        SqlExpression? checkExpression = null,
        string? checkExpressionText = null)
    {
        Name = name;
        Kind = kind;
        Columns = columns;
        ReferencedTable = referencedTable;
        ReferencedColumns = referencedColumns ?? [];
        OnDelete = onDelete;
        CheckExpression = checkExpression;
        CheckExpressionText = checkExpressionText;
    }

    /// <summary>Gets the explicit constraint name, or null for a generated name.</summary>
    public string? Name { get; }

    /// <summary>Gets the constraint kind.</summary>
    public SqlConstraintKind Kind { get; }

    /// <summary>Gets the constrained columns in key order.</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>Gets the parent table of a foreign key.</summary>
    public SqlTableReference? ReferencedTable { get; }

    /// <summary>Gets the parent key columns in matching order.</summary>
    public IReadOnlyList<string> ReferencedColumns { get; }

    /// <summary>Gets the parent deletion action; omitted actions default to restrict.</summary>
    public SqlReferentialAction OnDelete { get; }

    /// <summary>Gets the predicate of a check constraint.</summary>
    public SqlExpression? CheckExpression { get; }

    /// <summary>Gets the check predicate's SQL source without the enclosing parentheses.</summary>
    public string? CheckExpressionText { get; }
}
