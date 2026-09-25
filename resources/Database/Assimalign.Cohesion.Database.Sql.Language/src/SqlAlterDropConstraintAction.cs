namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Represents an ALTER TABLE DROP CONSTRAINT action.</summary>
public sealed class SqlAlterDropConstraintAction : SqlAlterAction
{
    internal SqlAlterDropConstraintAction(string constraintName) => ConstraintName = constraintName;

    /// <summary>Gets the constraint name being removed.</summary>
    public string ConstraintName { get; }
}
