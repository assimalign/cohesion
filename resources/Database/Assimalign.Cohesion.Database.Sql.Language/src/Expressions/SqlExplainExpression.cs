using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Describes a data statement without executing it.</summary>
public sealed class SqlExplainExpression : SqlQueryExpression
{
    internal SqlExplainExpression(SqlQueryExpression statement, Location? location)
        : base(SqlQueryCommandType.Explain, null, location) => Statement = statement;

    /// <summary>Gets the SELECT, INSERT, UPDATE, or DELETE statement to plan.</summary>
    public SqlQueryExpression Statement { get; }

    private protected override int ExpressionTreeDepth => Statement.NestingDepth;
}
