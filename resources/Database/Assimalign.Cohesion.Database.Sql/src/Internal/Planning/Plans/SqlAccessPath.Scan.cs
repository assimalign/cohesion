namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>The per-object table scan (the fallback for everything non-sargable).</summary>
internal sealed record SqlScanPath : SqlAccessPath
{
    internal static SqlScanPath Instance { get; } = new();
    internal SqlAccessCost? Cost { get; init; }
}
