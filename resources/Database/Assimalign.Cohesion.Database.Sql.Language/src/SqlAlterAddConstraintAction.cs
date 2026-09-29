namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Represents an ALTER TABLE ADD constraint action.</summary>
public sealed class SqlAlterAddConstraintAction : SqlAlterAction
{
    internal SqlAlterAddConstraintAction(SqlConstraintDefinition constraint) => Constraint = constraint;

    /// <summary>Gets the constraint being added.</summary>
    public SqlConstraintDefinition Constraint { get; }
}
