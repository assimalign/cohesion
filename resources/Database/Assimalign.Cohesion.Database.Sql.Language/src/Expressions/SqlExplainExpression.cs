using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Describes a data statement, optionally executing a SELECT to collect work counts.</summary>
public sealed class SqlExplainExpression : SqlQueryExpression
{
    internal SqlExplainExpression(SqlQueryExpression statement, Location? location, bool analyze = false)
        : base(SqlQueryCommandType.Explain, null, location) { Statement = statement; Analyze = analyze; }

    /// <summary>Gets the SELECT, INSERT, UPDATE, or DELETE statement to plan.</summary>
    public SqlQueryExpression Statement { get; }

    /// <summary>Gets whether the SELECT is executed to collect actual work. Data changes are refused.</summary>
    public bool Analyze { get; }

    private protected override int ExpressionTreeDepth => Statement.NestingDepth;
}
