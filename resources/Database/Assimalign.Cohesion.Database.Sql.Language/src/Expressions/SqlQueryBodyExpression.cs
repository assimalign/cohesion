using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Represents a SELECT or set operation wherever SQL accepts a query.</summary>
// Deviates from the repo interface-first rule per design decision: Database has concrete query variants (database-area.md).
public abstract class SqlQueryBodyExpression : SqlQueryExpression
{
    private IReadOnlyList<SqlCommonTableExpression> _commonTables = [];

    /// <summary>Initializes a query body.</summary>
    /// <param name="text">The raw query text.</param>
    /// <param name="location">The source location.</param>
    private protected SqlQueryBodyExpression(string? text, Location? location) : base(SqlQueryCommandType.Select, text, location) { }

    /// <summary>Gets the non-recursive WITH definitions in declaration order.</summary>
    public IReadOnlyList<SqlCommonTableExpression> CommonTables
    {
        get => _commonTables;
        internal set
        {
            _commonTables = value;
            foreach (var definition in value) { ExpressionDepth = System.Math.Max(ExpressionDepth, definition.Body.ExpressionDepth); }
        }
    }

    /// <summary>Gets the ordering of this query's result.</summary>
    public IReadOnlyList<SqlOrderByColumn> OrderBy { get; internal set; } = [];

    /// <summary>Gets the optional row limit.</summary>
    public SqlExpression? Limit { get; internal set; }

    /// <summary>Gets the limit spelling; TOP belongs only to SELECT operands.</summary>
    public SqlLimitSyntaxKind? LimitSyntaxKind { get; internal set; }

    /// <summary>Gets the optional number of rows to skip.</summary>
    public SqlExpression? Offset { get; internal set; }

    internal int ExpressionDepth { get; set; }

    /// <inheritdoc />
    private protected override int ExpressionTreeDepth => ExpressionDepth;

    internal void IncludeTailDepth()
    {
        ExpressionDepth = System.Math.Max(ExpressionDepth, System.Math.Max(SqlExpression.DepthOf(Limit), SqlExpression.DepthOf(Offset)));
        foreach (var order in OrderBy) { ExpressionDepth = System.Math.Max(ExpressionDepth, order.Expression.Depth); }
    }
}
