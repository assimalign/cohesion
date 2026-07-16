namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>
/// Represents relational catalog violations: duplicate or missing tables and
/// columns, and malformed persisted catalog records.
/// </summary>
public sealed class SqlCatalogException : DatabaseException
{
    /// <summary>
    /// Initializes a new <see cref="SqlCatalogException"/> with a message.
    /// </summary>
    /// <param name="message">A message describing the violation.</param>
    public SqlCatalogException(string message)
        : base(message)
    {
    }
}
