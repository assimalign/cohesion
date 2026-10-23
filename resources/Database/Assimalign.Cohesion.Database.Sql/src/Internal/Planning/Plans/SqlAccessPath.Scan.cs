namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>The per-object table scan (the fallback for everything non-sargable).</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlScanPath")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlScanPath.DebugView))]
internal sealed record SqlScanPath : SqlAccessPath
{
    internal static SqlScanPath Instance { get; } = new();
    internal SqlAccessCost? Cost { get; init; }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlScanPath value)
        {
            _ = value;
        }
    }
}
