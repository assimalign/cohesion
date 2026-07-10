using Microsoft.Build.Framework;

namespace Assimalign.Cohesion.Sdk.Database.Tasks;

/// <summary>
/// Generates an ordered migration script by diffing the compiled schema model
/// against the newest baseline under the project's migrations root.
/// </summary>
public sealed class CreateDatabaseMigrationTask : DatabaseTask
{
    /// <summary>
    /// The compiled schema model artifact produced by <see cref="CompileDatabaseSchemaTask"/>.
    /// </summary>
    [Required]
    public string SchemaModelPath { get; set; } = string.Empty;

    /// <summary>
    /// The directory generated migration scripts are written to.
    /// </summary>
    [Required]
    public string MigrationsRoot { get; set; } = string.Empty;

    /// <summary>
    /// The name of the migration to generate.
    /// </summary>
    public string? MigrationName { get; set; }

    /// <summary>
    /// The database model the project targets (Sql, Documents, Graph, ...).
    /// </summary>
    [Required]
    public string Model { get; set; } = string.Empty;

    /// <inheritdoc />
    public override bool Execute()
    {
        // Scaffold scope: the diff engine (schema model comparison, ordered
        // script emission, rollback pairing) is the L03.02.06 tooling work.
        Log.LogWarning(
            $"Migration generation for the '{Model}' model is not implemented yet " +
            "(tracked by the Database developer-tooling epic, L03.02.06). " +
            $"Requested migration: '{MigrationName ?? "<unnamed>"}'.");
        return true;
    }
}
