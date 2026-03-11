namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// Configures a SQL database engine instance.
/// </summary>
public sealed class SqlDatabaseEngineOptions
{
    /// <summary>
    /// Gets or sets the logical engine name.
    /// </summary>
    public string? EngineName { get; set; }

    /// <summary>
    /// Gets or sets the root directory where per-database files are created.
    /// </summary>
    /// <remarks>
    /// When <see cref="StorageStrategy"/> is null and <see cref="RootPath"/> is provided,
    /// a file-based strategy is used automatically. When both are null, an in-memory
    /// strategy is used.
    /// </remarks>
    public string? RootPath { get; set; }

    /// <summary>
    /// Gets or sets the storage strategy for creating and opening database storage.
    /// </summary>
    /// <remarks>
    /// When null, the engine selects a default strategy based on <see cref="RootPath"/>:
    /// file-based if a path is provided, or in-memory otherwise.
    /// </remarks>
    public ISqlStorageStrategy? StorageStrategy { get; set; }
}
