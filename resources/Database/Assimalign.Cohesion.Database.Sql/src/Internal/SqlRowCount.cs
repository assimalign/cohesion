using System;
using System.Globalization;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A bound row count, initialized once for each execution of a relation.</summary>
internal sealed record SqlRowCount(SqlBoundExpression? Expression, string Clause, Location? Location, long Count = 0)
{
    internal SqlRowCount Initialize(SqlExpressionEvaluator evaluator)
    {
        if (Expression is null) { return this; }
        object? value = evaluator.Evaluate(Expression, []);
        long count = value switch
        {
            sbyte number when number >= 0 => number,
            short number when number >= 0 => number,
            int number when number >= 0 => number,
            long number when number >= 0 => number,
            _ => throw new SqlPlanningException("COHSQLE018", string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.CountNonNegative, Clause), Location),
        };
        return this with { Expression = null, Count = count };
    }

    public override string ToString() => Expression is null ? Count.ToString(CultureInfo.InvariantCulture)
        : Expression is SqlBoundConstant constant ? Convert.ToString(constant.Value, CultureInfo.InvariantCulture) ?? "NULL" : "expression";
}
