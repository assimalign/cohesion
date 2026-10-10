namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// What binding a function call needs from outside the statement: the engine's frozen function
/// catalog, and the database the statement runs in, which a bound call hands to its function's
/// context. One instance per open database, held by its <see cref="SqlBoundTableCache"/>.
/// </summary>
internal sealed class SqlFunctionEnvironment
{
    /// <summary>Initializes an environment.</summary>
    /// <param name="catalog">The engine's function catalog.</param>
    /// <param name="database">The database; empty for a scope outside any database.</param>
    internal SqlFunctionEnvironment(SqlFunctionCatalog catalog, DatabaseName database)
    {
        Catalog = catalog;
        Database = database;
    }

    /// <summary>
    /// Gets the standard library outside any database: the environment of a binding scope created
    /// without one, as tests and the engine created by <see cref="SqlDatabaseEngine.Create"/> use.
    /// </summary>
    internal static SqlFunctionEnvironment Standard { get; } = new(SqlFunctionCatalog.Standard, default);

    /// <summary>Gets the engine's function catalog.</summary>
    internal SqlFunctionCatalog Catalog { get; }

    /// <summary>Gets the database the statement runs in.</summary>
    internal DatabaseName Database { get; }
}
