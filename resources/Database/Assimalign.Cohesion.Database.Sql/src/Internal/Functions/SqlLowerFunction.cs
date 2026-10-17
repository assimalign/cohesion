using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// <c>LOWER(value)</c>: a string in lower case, invariant culture. Any other value passes through
/// unchanged, as it did before typed signatures (owner decision 67), so its parameter and result are
/// <see cref="SqlType.AnyElement"/>.
/// </summary>
internal sealed class SqlLowerFunction : SqlScalarFunction
{
    /// <summary>Initializes the function.</summary>
    internal SqlLowerFunction()
        : base("LOWER", [SqlType.AnyElement], SqlType.AnyElement, SqlFunctionVolatility.Immutable)
    {
        Usage = "LOWER(value)";
        ResultRule = SqlResultRule.TextElement;
    }

    /// <inheritdoc />
    protected override SqlValue InvokeCore(scoped in SqlArguments arguments)
    {
        var value = arguments[0];
        return value.Type is DatabaseType.String or DatabaseType.Character ? SqlValue.FromString((value.Type == DatabaseType.Character ? value.AsString().TrimEnd(' ') : value.AsString()).ToLowerInvariant()) : value;
    }
}
