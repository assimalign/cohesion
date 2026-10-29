using System;
using System.Linq;

namespace Assimalign.Cohesion.Database.Sql.Language;

public static partial class SqlExpressionRenderer
{
    /// <summary>Renders a normalized column or table constraint as a table constraint.</summary>
    /// <param name="constraint">The parsed constraint.</param>
    /// <returns>Canonical SQL with explicit DELETE then UPDATE actions for a foreign key.</returns>
    /// <exception cref="ArgumentNullException">The constraint is null.</exception>
    /// <exception cref="NotSupportedException">The constraint or action is unknown.</exception>
    /// <exception cref="InsufficientExecutionStackException">The CHECK predicate exceeds available stack.</exception>
    public static string Render(SqlConstraintDefinition constraint)
    {
        ArgumentNullException.ThrowIfNull(constraint);
        string prefix = constraint.Name is { } name ? "CONSTRAINT " + Identifier(name) + " " : "";
        string columns = " (" + string.Join(", ", constraint.Columns.Select(Identifier)) + ")";
        return prefix + (constraint.Kind switch
        {
            SqlConstraintKind.PrimaryKey => "PRIMARY KEY" + columns,
            SqlConstraintKind.Unique => "UNIQUE" + columns,
            SqlConstraintKind.Check => "CHECK (" + Render(constraint.CheckExpression!) + ")",
            SqlConstraintKind.ForeignKey => "FOREIGN KEY" + columns + " REFERENCES " +
                SequenceName(constraint.ReferencedTable!) + " (" + string.Join(", ", constraint.ReferencedColumns.Select(Identifier)) +
                ") ON DELETE " + ReferentialAction(constraint.OnDelete) + " ON UPDATE " + ReferentialAction(constraint.OnUpdate),
            _ => throw new NotSupportedException(ErrorMessages.ConstraintRenderingUnknown),
        });
    }

    private static string ReferentialAction(SqlReferentialAction action) => action switch
    {
        SqlReferentialAction.Restrict => "RESTRICT",
        SqlReferentialAction.Cascade => "CASCADE",
        SqlReferentialAction.NoAction => "NO ACTION",
        SqlReferentialAction.SetNull => "SET NULL",
        SqlReferentialAction.SetDefault => "SET DEFAULT",
        _ => throw new NotSupportedException(ErrorMessages.ConstraintRenderingUnknown),
    };
}
