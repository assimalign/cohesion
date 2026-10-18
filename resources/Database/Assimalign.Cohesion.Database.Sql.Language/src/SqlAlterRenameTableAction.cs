namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Renames a table within its SQL schema.</summary>
public sealed class SqlAlterRenameTableAction : SqlAlterAction
{
    internal SqlAlterRenameTableAction(string newName)
    {
        NewName = newName;
    }

    /// <summary>Gets the new table name.</summary>
    public string NewName { get; }
}
