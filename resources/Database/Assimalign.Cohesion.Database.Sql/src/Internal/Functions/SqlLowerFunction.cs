using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// <c>LOWER(TEXT)</c>: invariant lower case; CHAR converts to TEXT before the call.
/// </summary>
internal sealed class SqlLowerFunction : SqlScalarFunction
{
    /// <summary>Initializes the function.</summary>
    internal SqlLowerFunction()
        : base("LOWER", [SqlType.Text], SqlType.Text, SqlFunctionVolatility.Immutable)
    {
        Usage = "LOWER(TEXT)";
        ResultRule = SqlResultRule.TextElement;
    }

    /// <inheritdoc />
    protected override SqlValue InvokeCore(scoped in SqlArguments arguments)
    {
        return SqlBuiltinScalarFunction.ChangeCase(in arguments, upper: false);
    }
}
