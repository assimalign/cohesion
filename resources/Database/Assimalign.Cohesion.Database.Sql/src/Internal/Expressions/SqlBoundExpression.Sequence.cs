namespace Assimalign.Cohesion.Database.Sql.Internal;

[global::System.Diagnostics.DebuggerDisplay("SqlBoundSequence")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class SqlBoundSequence : SqlBoundExpression
{
    internal SqlBoundSequence(string operation, SqlBoundExpression[] arguments) : base(SqlBoundExpressionKind.Sequence)
    { Operation = operation; Arguments = arguments; }

    internal string Operation { get; }
    internal SqlBoundExpression[] Arguments { get; }

    private sealed class DebugView
    { public DebugView(SqlBoundSequence sequence) { _ = sequence; } }
}
