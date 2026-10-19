namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>The constant-time reconciliation marker for an ordered declared-script set.</summary>
/// <param name="ContentHash">The SHA-256 of the ordered names and body digests.</param>
/// <param name="Generation">The catalog generation at successful reconciliation.</param>
public sealed record SqlCatalogScriptState(string ContentHash, ulong Generation);
