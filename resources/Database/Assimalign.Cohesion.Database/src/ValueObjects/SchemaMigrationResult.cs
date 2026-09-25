namespace Assimalign.Cohesion.Database;

/// <summary>Describes the outcome of applying a compiled schema.</summary>
/// <param name="FromHash">The previously applied hash, or null for an empty catalog.</param>
/// <param name="ToHash">The desired schema hash.</param>
/// <param name="OperationCount">The number of migration operations applied.</param>
/// <param name="WasAlreadyApplied">Whether the desired schema was already applied.</param>
public readonly record struct SchemaMigrationResult(
    string? FromHash,
    string ToHash,
    int OperationCount,
    bool WasAlreadyApplied);
