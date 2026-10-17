using System;

namespace Assimalign.Cohesion.Database.Sql.AotSample;

/// <summary>
/// A hand-written strict scalar leaf, the design's <c>clamp(BIGINT, BIGINT, BIGINT)</c>: it derives
/// from the base the built-in <c>ABS</c> derives from and reads its arguments through the value ABI.
/// <see cref="Math.Clamp(long, long, long)"/> throws when low exceeds high, which the engine codes as
/// <c>COHSQLE007</c>.
/// </summary>
internal sealed class ClampFunction : SqlScalarFunction
{
    public ClampFunction()
        : base("clamp", [SqlType.BigInt, SqlType.BigInt, SqlType.BigInt], SqlType.BigInt, SqlFunctionVolatility.Immutable)
    {
    }

    protected override SqlValue InvokeCore(scoped in SqlArguments arguments)
        => SqlValue.FromInt64(Math.Clamp(arguments.GetInt64(0), arguments.GetInt64(1), arguments.GetInt64(2)));
}
