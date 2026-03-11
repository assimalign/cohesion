using System;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// Creates SQL database engine instances.
/// </summary>
public static class SqlDatabaseEngineFactory
{
    /// <summary>
    /// Creates a SQL database engine for the specified root path.
    /// </summary>
    /// <param name="rootPath">Directory containing database file sets.</param>
    /// <param name="engineName">Optional logical engine name.</param>
    /// <returns>A new SQL engine instance.</returns>
    public static SqlDatabaseEngine Create(string rootPath, string? engineName = null)
    {
        return Create(new SqlDatabaseEngineOptions
        {
            RootPath = rootPath,
            EngineName = engineName,
        });
    }

    /// <summary>
    /// Creates a SQL database engine from options.
    /// </summary>
    /// <param name="options">Engine creation options.</param>
    /// <returns>A new SQL engine instance.</returns>
    public static SqlDatabaseEngine Create(SqlDatabaseEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return SqlDatabaseEngine.Create(options);
    }
}
