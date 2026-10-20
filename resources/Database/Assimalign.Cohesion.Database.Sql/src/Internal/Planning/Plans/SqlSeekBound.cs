namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>One endpoint of a seek's range component.</summary>
/// <param name="Value">The bound's value, coerced to the key column's storage type.</param>
/// <param name="Inclusive">Whether the bound itself is included.</param>
internal readonly record struct SqlSeekBound(object? Value, bool Inclusive);
