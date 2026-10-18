namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>NULLIF and EXTRACT retain evaluator semantics outside the function ABI.</summary>
internal sealed class SqlBoundSpecialForm : SqlBoundExpression
{
    internal SqlBoundSpecialForm(string name, SqlBoundExpression[] arguments, SqlBoundCollation collation)
        : base(SqlBoundExpressionKind.SpecialForm)
    {
        Name = name;
        Arguments = arguments;
        Collation = collation;
    }

    internal string Name { get; }
    internal SqlBoundExpression[] Arguments { get; }
    internal SqlBoundCollation Collation { get; }
}
