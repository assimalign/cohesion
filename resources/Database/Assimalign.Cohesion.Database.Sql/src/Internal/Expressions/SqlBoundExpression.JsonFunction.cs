using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

[System.Diagnostics.DebuggerDisplay("SQL/JSON function")]
[System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class SqlBoundJsonFunction : SqlBoundExpression
{
    internal SqlBoundJsonFunction(SqlJsonFunctionExpression syntax, SqlBoundExpression context, SqlJsonPath? path,
        SqlBoundCast conversion, SqlBoundExpression? emptyDefault, SqlBoundExpression? errorDefault)
        : base(SqlBoundExpressionKind.JsonFunction)
    { Syntax = syntax; Context = context; Path = path; Conversion = conversion; EmptyDefault = emptyDefault; ErrorDefault = errorDefault; }
    internal SqlJsonFunctionExpression Syntax { get; }
    internal SqlBoundExpression Context { get; }
    internal SqlJsonPath? Path { get; }
    internal SqlBoundCast Conversion { get; }
    internal SqlBoundExpression? EmptyDefault { get; }
    internal SqlBoundExpression? ErrorDefault { get; }
    private sealed class DebugView { public DebugView(SqlBoundJsonFunction value) { _ = value; } }
}
