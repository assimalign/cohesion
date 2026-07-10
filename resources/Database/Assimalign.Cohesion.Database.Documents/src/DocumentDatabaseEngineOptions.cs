namespace Assimalign.Cohesion.Database.Documents;

/// <summary>
/// Options for creating a <see cref="DocumentDatabaseEngine"/>.
/// </summary>
public sealed class DocumentDatabaseEngineOptions
{
    /// <summary>
    /// Gets or sets the logical name of the engine instance.
    /// </summary>
    public string? EngineName { get; set; }

    /// <summary>
    /// Gets or sets the root directory database files are created under.
    /// When null, databases are created in memory.
    /// </summary>
    public string? RootPath { get; set; }
}
