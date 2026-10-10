using System;
using System.Globalization;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// <c>ABS(numeric)</c>: a number's magnitude. Exact integers widen to BIGINT before the magnitude is
/// taken (so INTEGER's minimum is representable); approximate and decimal values keep their type.
/// A non-number is refused only when the call runs, as before typed signatures (owner decision 67),
/// so the parameter is <see cref="SqlType.AnyElement"/> and the planner types the result by
/// <see cref="SqlResultRule.NumericElement"/>.
/// </summary>
internal sealed class SqlAbsFunction : SqlScalarFunction
{
    /// <summary>Initializes the function.</summary>
    internal SqlAbsFunction()
        : base("ABS", [SqlType.AnyElement], SqlType.AnyElement, SqlFunctionVolatility.Immutable)
    {
        Usage = "ABS(numeric)";
        ResultRule = SqlResultRule.NumericElement;
    }

    /// <inheritdoc />
    /// <exception cref="SqlEvaluationException">The BIGINT minimum has no positive counterpart (<c>COHSQLE002</c>).</exception>
    /// <exception cref="DatabaseException">The value is not a number.</exception>
    protected override SqlValue InvokeCore(scoped in SqlArguments arguments)
    {
        var value = arguments[0];
        switch (value.Type)
        {
            case DatabaseType.Int8:
                return SqlValue.FromInt64(Math.Abs((long)value.AsSByte()));
            case DatabaseType.Int16:
                return SqlValue.FromInt64(Math.Abs((long)value.AsInt16()));
            case DatabaseType.Int32:
                return SqlValue.FromInt64(Math.Abs((long)value.AsInt32()));
            case DatabaseType.Int64:
                long number = value.AsInt64();
                return number == long.MinValue
                    ? throw SqlEvaluationException.NumericValueOutOfRange($"ABS of BIGINT {number.ToString(CultureInfo.InvariantCulture)} overflowed.")
                    : SqlValue.FromInt64(Math.Abs(number));
            case DatabaseType.Float32:
                return SqlValue.FromSingle(Math.Abs(value.AsSingle()));
            case DatabaseType.Float64:
                return SqlValue.FromDouble(Math.Abs(value.AsDouble()));
            case DatabaseType.Decimal:
                return SqlValue.FromDecimal(Math.Abs(value.AsDecimal()));
            default:
                throw new DatabaseException("ABS requires a numeric argument.");
        }
    }
}
