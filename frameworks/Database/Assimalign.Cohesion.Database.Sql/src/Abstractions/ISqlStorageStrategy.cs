namespace Assimalign.Cohesion.Database.Sql;

using Assimalign.Cohesion.Database.Sql.Storage;

/// <summary>
/// Defines a strategy for creating and opening the three storage streams
/// (data, journal, backup) for a SQL database.
/// </summary>
/// <remarks>
/// Implementations allow the SQL engine to swap between file-based storage
/// (for production) and in-memory storage (for testing and embedded scenarios).
/// </remarks>
public interface ISqlStorageStrategy
{
    /// <summary>
    /// Creates new storage streams for a database.
    /// </summary>
    /// <param name="databaseName">The name of the database to create storage for.</param>
    /// <returns>A new <see cref="SqlStorage"/> instance.</returns>
    /// <exception cref="DatabaseException">Thrown when storage already exists for the specified name.</exception>
    SqlStorage CreateStorage(string databaseName);

    /// <summary>
    /// Opens existing storage streams for a database.
    /// </summary>
    /// <param name="databaseName">The name of the database to open storage for.</param>
    /// <returns>An existing <see cref="SqlStorage"/> instance.</returns>
    /// <exception cref="DatabaseException">Thrown when storage does not exist for the specified name.</exception>
    SqlStorage OpenStorage(string databaseName);

    /// <summary>
    /// Drops all storage assets for a database.
    /// </summary>
    /// <param name="databaseName">The name of the database to drop storage for.</param>
    void DropStorage(string databaseName);

    /// <summary>
    /// Returns whether storage exists for the specified database.
    /// </summary>
    /// <param name="databaseName">The name of the database to check.</param>
    /// <returns><c>true</c> if storage exists; otherwise <c>false</c>.</returns>
    bool StorageExists(string databaseName);
}
