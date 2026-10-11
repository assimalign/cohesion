namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// What binding a statement needs from outside it: the engine's frozen function catalog, its frozen
/// type catalog (the domains DDL and <c>CAST</c> name and the casts <c>CAST</c> applies), and the
/// database the statement runs in, which a bound call hands to its function's context. One instance
/// per open database, held by its <see cref="SqlBoundTableCache"/>.
/// </summary>
internal sealed class SqlFunctionEnvironment
{
    /// <summary>Initializes an environment.</summary>
    /// <param name="catalog">The engine's function catalog.</param>
    /// <param name="database">The database; empty for a scope outside any database.</param>
    /// <param name="types">The engine's type catalog; the built-in types and casts alone when null.</param>
    internal SqlFunctionEnvironment(SqlFunctionCatalog catalog, DatabaseName database, SqlTypeCatalog? types = null)
    {
        Catalog = catalog;
        Database = database;
        Types = types ?? SqlTypeCatalog.Standard;
    }

    /// <summary>
    /// Gets the standard library outside any database: the environment of a binding scope created
    /// without one, as tests and the engine created by <see cref="SqlDatabaseEngine.Create"/> use.
    /// </summary>
    internal static SqlFunctionEnvironment Standard { get; } = new(SqlFunctionCatalog.Standard, default, SqlTypeCatalog.Standard);

    /// <summary>Gets the engine's function catalog.</summary>
    internal SqlFunctionCatalog Catalog { get; }

    /// <summary>Gets the engine's type catalog: the built-in types, the domains and the cast registry.</summary>
    internal SqlTypeCatalog Types { get; }

    /// <summary>Gets the database the statement runs in.</summary>
    internal DatabaseName Database { get; }

    /// <summary>
    /// Gets whether a statement may run an application's code whose failure depends on its
    /// cancellation token: a registered function or a domain's check, which read it, or an
    /// application's cast, whose cancellation is the statement's only when the token says so.
    /// </summary>
    internal bool RunsApplicationCode => Catalog.HasApplicationFunctions || Types.HasDomainChecks || Types.HasApplicationCasts;
}
