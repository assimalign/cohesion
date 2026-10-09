using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// <c>UPPER(value)</c>: a string in upper case, invariant culture. Any other value passes through
/// unchanged, as it did before typed signatures (owner decision 67), so its parameter and result are
/// <see cref="SqlType.AnyElement"/>.
/// </summary>
internal sealed class SqlUpperFunction : SqlScalarFunction
{
    /// <summary>Initializes the function.</summary>
    internal SqlUpperFunction()
        : base("UPPER", [SqlType.AnyElement], SqlType.AnyElement, SqlFunctionVolatility.Immutable)
    {
        Usage = "UPPER(value)";
    }

    /// <inheritdoc />
    protected override SqlValue InvokeCore(scoped in SqlArguments arguments)
    {
        var value = arguments[0];
        return value.Type == DatabaseType.String ? SqlValue.FromString(value.AsString().ToUpperInvariant()) : value;
    }
}
