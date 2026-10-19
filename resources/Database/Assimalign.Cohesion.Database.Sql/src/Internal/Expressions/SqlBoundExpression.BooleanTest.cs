using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>The bound BooleanTest predicate.</summary>
internal sealed class SqlBoundBooleanTest : SqlBoundExpression
{
    internal SqlBoundBooleanTest(SqlBoundExpression operand, SqlTruthValue truthValue, bool isNegated, Location? location = null)
        : base(SqlBoundExpressionKind.BooleanTest)
    {
        Location = location;
        Operand = operand;
        TruthValue = truthValue;
        IsNegated = isNegated;
    }

    internal SqlBoundExpression Operand { get; }

    internal SqlTruthValue TruthValue { get; }

    internal bool IsNegated { get; }

    internal Location? Location { get; }
}
