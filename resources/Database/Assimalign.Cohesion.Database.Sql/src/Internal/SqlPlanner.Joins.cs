using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    /// <summary>Binds both stored inputs into one unambiguous column scope.</summary>
    private IReadOnlyList<SqlTableBinding> BindJoin(SqlSelectExpression select, SqlCatalogTable left)
    {
        // The language capability check rejects unsupported forms before planning.
        if (select.Joins.Count != 1 || select.Joins[0] is not { JoinType: SqlJoinType.Inner, Condition: not null })
        {
            throw new DatabaseException("Only a two-table INNER JOIN with an ON predicate can be planned.");
        }

        var leftReference = select.From!;
        var rightReference = select.Joins[0].Table;
        var right = ResolveTable(rightReference);
        string leftName = leftReference.Alias ?? left.Name;
        string rightName = rightReference.Alias ?? right.Name;
        if (string.Equals(leftName, rightName, StringComparison.OrdinalIgnoreCase)
            && (leftReference.Alias is not null || rightReference.Alias is not null
                || string.Equals(left.Schema, right.Schema, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DatabaseException($"Duplicate table name or alias '{leftName}'; use distinct aliases for a self join.");
        }

        return [new SqlTableBinding(left, leftReference, 0), new SqlTableBinding(right, rightReference, left.Columns.Count)];
    }

    /// <summary>
    /// Chooses the longest safe equality prefix on either input. Ties prefer a
    /// unique index, then its name, then the right input. Every ON conjunct is
    /// retained as a residual; disjunctions and computed keys use nested loops.
    /// </summary>
    private SqlJoinIndexPath? SelectJoinAccessPath(
        IReadOnlyList<SqlTableBinding> bindings, SqlExpression condition, SqlExpressionEvaluator evaluator)
    {
        var equalities = new List<(int Left, int Right, Collation Collation)>();
        CollectJoinEqualities(condition, evaluator, equalities);
        SqlJoinIndexPath? best = null;

        foreach (int inner in new[] { 1, 0 })
        {
            var innerBinding = bindings[inner];
            var outerBinding = bindings[1 - inner];
            foreach (var index in _catalog.GetIndexes(innerBinding.Table.ObjectId).OrderBy(index => index.Name, StringComparer.OrdinalIgnoreCase))
            {
                var outerOrdinals = new List<int>();
                foreach (string key in index.ColumnNames)
                {
                    int innerOrdinal = FindColumnOrdinal(innerBinding.Table, key);
                    int joinedOrdinal = innerBinding.Offset + innerOrdinal;
                    int outerOrdinal = -1;
                    foreach (var equality in equalities)
                    {
                        int candidate = equality.Left == joinedOrdinal ? equality.Right
                            : equality.Right == joinedOrdinal ? equality.Left : -1;
                        int local = candidate - outerBinding.Offset;
                        if (local >= 0 && local < outerBinding.Table.Columns.Count
                            && CanSeekJoinEquality(innerBinding.Table.Columns[innerOrdinal].Type.Type,
                                outerBinding.Table.Columns[local].Type.Type)
                            && (innerBinding.Table.Columns[innerOrdinal].Type.Type is not (DatabaseType.String or DatabaseType.Json)
                                || equality.Collation.IsIndexBacked
                                    && equality.Collation == (innerBinding.Table.Columns[innerOrdinal].Collation ?? _catalog.DefaultCollation)))
                        {
                            outerOrdinal = local;
                            break;
                        }
                    }

                    if (outerOrdinal < 0)
                    {
                        break;
                    }
                    outerOrdinals.Add(outerOrdinal);
                }

                if (outerOrdinals.Count == 0)
                {
                    continue;
                }

                if (best is null || outerOrdinals.Count > best.OuterOrdinals.Count
                    || (outerOrdinals.Count == best.OuterOrdinals.Count
                        && (index.IsUnique && !best.Index.IsUnique
                            || index.IsUnique == best.Index.IsUnique
                                && StringComparer.OrdinalIgnoreCase.Compare(index.Name, best.Index.Name) < 0)))
                {
                    best = new SqlJoinIndexPath(inner, index, outerOrdinals);
                }
            }
        }

        return best;
    }

    /// <summary>Collects only mandatory column equalities, never OR alternatives.</summary>
    private static void CollectJoinEqualities(SqlExpression expression, SqlExpressionEvaluator evaluator,
        List<(int Left, int Right, Collation Collation)> equalities)
    {
        if (expression is SqlBinaryExpression { Operator: SqlBinaryOperator.And } conjunction)
        {
            CollectJoinEqualities(conjunction.Left, evaluator, equalities);
            CollectJoinEqualities(conjunction.Right, evaluator, equalities);
        }
        else if (expression is SqlBinaryExpression { Operator: SqlBinaryOperator.Equal } binary
            && UnwrapCollation(binary.Left) is SqlColumnReferenceExpression left
            && UnwrapCollation(binary.Right) is SqlColumnReferenceExpression right)
        {
            equalities.Add((evaluator.ResolveColumn(left), evaluator.ResolveColumn(right), evaluator.ResolveCollation(binary.Left, binary.Right)));
        }
    }

    /// <summary>
    /// Requires key equality to match evaluator equality. Floating values can
    /// collapse on decimal comparison; timestamps encode kind/offset tie breakers
    /// ignored by comparison. Those types must scan even with an index present.
    /// </summary>
    private static bool CanSeekJoinEquality(DatabaseType inner, DatabaseType outer)
    {
        static bool ExactNumeric(DatabaseType type) => type is DatabaseType.Int8 or DatabaseType.Int16
            or DatabaseType.Int32 or DatabaseType.Int64 or DatabaseType.Decimal;

        return ExactNumeric(inner) && ExactNumeric(outer)
            || inner == outer && inner is DatabaseType.Boolean or DatabaseType.String or DatabaseType.Json
                or DatabaseType.Date or DatabaseType.Time or DatabaseType.TimeSpan or DatabaseType.Guid;
    }
}
