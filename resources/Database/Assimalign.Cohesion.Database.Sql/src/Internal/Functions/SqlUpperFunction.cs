using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// <c>UPPER(TEXT)</c>: invariant upper case; CHAR converts to TEXT before the call.
/// </summary>
internal sealed class SqlUpperFunction : SqlScalarFunction
{
    /// <summary>Initializes the function.</summary>
    internal SqlUpperFunction()
        : base("UPPER", [SqlType.Text], SqlType.Text, SqlFunctionVolatility.Immutable)
    {
        Usage = "UPPER(TEXT)";
        ResultRule = SqlResultRule.TextElement;
    }

    /// <inheritdoc />
    protected override SqlValue InvokeCore(scoped in SqlArguments arguments)
    {
        return SqlBuiltinScalarFunction.ChangeCase(in arguments, upper: true);
    }
}
