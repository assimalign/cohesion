namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>Describes the outcome of applying a SQL compiled schema to a database.</summary>
/// <remarks>
/// Moved from the area root, where it was <c>SchemaMigrationResult</c> (owner decision 50 of
/// 2026-10-09): the root keeps no schema type, and only the SQL model provisions schemas.
/// </remarks>
/// <param name="FromHash">The previously applied hash, or null for a database with no recorded schema.</param>
/// <param name="ToHash">The desired schema hash.</param>
/// <param name="OperationCount">The number of migration operations applied.</param>
/// <param name="WasAlreadyApplied">Whether the desired schema was already applied, so nothing ran.</param>
public readonly record struct SqlSchemaMigrationResult(
    string? FromHash,
    string ToHash,
    int OperationCount,
    bool WasAlreadyApplied);
