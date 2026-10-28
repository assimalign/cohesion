using System.Collections.Generic;
using System.Globalization;

using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A lexical scope, searched before its parent; views share the same execution frame.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlCorrelationScope")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlCorrelationScope.DebugView))]
internal sealed class SqlCorrelationScope
{
    private readonly object? _groupBy;

    internal SqlCorrelationScope(int id, SqlExpressionEvaluator local, SqlCorrelationScope? parent,
        IReadOnlyList<SqlExpression>? groupBy = null, List<SqlBoundGrouping>? groupingCalls = null)
    {
        Id = id;
        Local = local;
        Parent = parent;
        _groupBy = groupingCalls is null ? groupBy : new GroupingState(groupBy!, groupingCalls);
    }

    internal IReadOnlyList<SqlRelationBinding>? Bindings { get; init; }
    internal int Id { get; }
    internal SqlExpressionEvaluator Local { get; }
    internal SqlCorrelationScope? Parent { get; }
    internal IReadOnlyList<SqlExpression>? GroupBy => _groupBy is GroupingState grouping ? grouping.Keys : _groupBy as IReadOnlyList<SqlExpression>;
    internal List<SqlBoundGrouping>? GroupingCalls => (_groupBy as GroupingState)?.Calls;

    internal SqlOuterReference Resolve(SqlColumnReferenceExpression column)
    {
        try { Local.ResolveLocalColumn(column); }
        catch (SqlPlanningException exception) when (exception.UnresolvedColumn is not null &&
            !exception.HasLocalQualifier && Parent is not null)
        {
            return Parent.Resolve(column);
        }
        var value = Local.Bind(column);
        if (GroupBy is not null)
        {
            int key = -1;
            for (int i = 0; i < GroupBy.Count; i++)
            {
                if (GroupBy[i] is not SqlColumnReferenceExpression reference) { continue; }
                SqlBoundExpression grouped;
                try { Local.ResolveLocalColumn(reference); grouped = Local.Bind(reference); }
                catch (SqlPlanningException exception) when (exception.UnresolvedColumn is not null)
                {
                    // A key belonging to an ancestor scope cannot group this scope's local column.
                    continue;
                }
                // USING may select the other input or coerce the merged name. Physical lookup
                // ordinals alone do not identify the bound grouping value (parse_clause.c:1722).
                if ((value is SqlBoundColumn a && grouped is SqlBoundColumn b && a.Ordinal == b.Ordinal ||
                    ReferenceEquals(value, grouped)) && Local.ColumnMetadata(column).Type.Type == Local.ColumnMetadata(reference).Type.Type)
                {
                    key = i;
                    break;
                }
            }
            if (key < 0)
            {
                throw new SqlPlanningException("CS05017", string.Format(CultureInfo.CurrentCulture,
                    ErrorMessages.CorrelatedUngroupedColumn, column.ColumnName), column.Location);
            }
            value = new SqlBoundSlot(key);
        }
        return new SqlOuterReference(Id, value, Local.ColumnMetadata(column));
    }

    private sealed record GroupingState(IReadOnlyList<SqlExpression> Keys, List<SqlBoundGrouping> Calls);

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlCorrelationScope value)
        {
            _ = value;
        }
    }
}
