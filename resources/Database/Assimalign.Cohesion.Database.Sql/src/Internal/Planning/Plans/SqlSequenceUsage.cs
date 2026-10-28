using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Internal;

[global::System.Diagnostics.DebuggerDisplay("SqlSequenceUsage")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class SqlSequenceUsage
{
    internal static readonly object LastValue = new();
    internal List<object?> Names { get; } = new();
    internal bool DynamicNames { get; set; }
    internal bool ContainsLike { get; set; }
    internal SqlExpressionEvaluator? Evaluator { get; set; }

    private sealed class DebugView
    { public DebugView(SqlSequenceUsage usage) { _ = usage; } }
}
