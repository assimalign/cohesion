using System.Collections.Generic;
using System.Globalization;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A lexical scope, searched before its parent; views share the same execution frame.</summary>
internal sealed class SqlCorrelationScope
{
    internal SqlCorrelationScope(int id, SqlExpressionEvaluator local, SqlCorrelationScope? parent,
        IReadOnlyList<SqlExpression>? groupBy = null)
    {
        Id = id;
        Local = local;
        Parent = parent;
        GroupBy = groupBy;
    }

    internal IReadOnlyList<SqlRelationBinding>? Bindings { get; init; }
    internal int Id { get; }
    internal SqlExpressionEvaluator Local { get; }
    internal SqlCorrelationScope? Parent { get; }
    internal IReadOnlyList<SqlExpression>? GroupBy { get; }

    internal SqlOuterReference Resolve(SqlColumnReferenceExpression column)
    {
        int ordinal;
        try { ordinal = Local.ResolveLocalColumn(column); }
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
                int groupedOrdinal;
                try { groupedOrdinal = Local.ResolveLocalColumn(reference); }
                catch (SqlPlanningException exception) when (exception.UnresolvedColumn is not null)
                {
                    // A key belonging to an ancestor scope cannot group this scope's local column.
                    continue;
                }
                if (groupedOrdinal == ordinal)
                {
                    key = i;
                    break;
                }
            }
            if (key < 0)
            {
                throw new SqlPlanningException("COHSQLE017", string.Format(CultureInfo.CurrentCulture,
                    ErrorMessages.CorrelatedUngroupedColumn, column.ColumnName), column.Location);
            }
            value = new SqlBoundSlot(key);
        }
        return new SqlOuterReference(Id, value, Local.ColumnMetadata(column));
    }
}

internal sealed record SqlOuterReference(int Scope, SqlBoundExpression Value, SqlCatalogColumn Column);
