namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>NULLIF and EXTRACT retain evaluator semantics outside the function ABI.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlBoundSpecialForm")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundSpecialForm.DebugView))]
internal sealed class SqlBoundSpecialForm : SqlBoundExpression
{
    internal SqlBoundSpecialForm(string name, SqlBoundExpression[] arguments, SqlBoundCollation collation, bool firstCharacterComparand = false, bool secondCharacterComparand = false)
        : base(SqlBoundExpressionKind.SpecialForm)
    {
        Name = name;
        Arguments = arguments;
        Collation = collation;
        FirstCharacterComparand = firstCharacterComparand;
        SecondCharacterComparand = secondCharacterComparand;
    }

    internal bool FirstCharacterComparand { get; }
    internal bool SecondCharacterComparand { get; }
    internal string Name { get; }
    internal SqlBoundExpression[] Arguments { get; }
    internal SqlBoundCollation Collation { get; }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundSpecialForm value)
        {
            _ = value;
        }
    }
}
