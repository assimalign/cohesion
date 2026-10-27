using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    private CommonTableState? _commonTableState;

    private static IReadOnlyList<SqlCommonTableExpression> CommonTablesOf(SqlQueryExpression query) => query switch
    {
        SqlQueryBodyExpression select => select.CommonTables,
        SqlInsertExpression insert => insert.CommonTables,
        SqlUpdateExpression update => update.CommonTables,
        SqlDeleteExpression delete => delete.CommonTables,
        _ => [],
    };

    // Determine syntactic reachability before planning evaluates any count or seek bound.
    // The normal binder still checks every definition, including all unknown names/types.
    private static HashSet<SqlCommonTableExpression>? FindReferencedCommonTables(SqlQueryExpression statement)
    {
        if (CommonTablesOf(statement).Count == 0 && statement is not SqlInsertExpression { SelectSource.CommonTables.Count: > 0 })
        {
            return null;
        }
        return FindReferencedCommonTablesCore(statement);
    }

    private static HashSet<SqlCommonTableExpression> FindReferencedCommonTablesCore(SqlQueryExpression statement)
    {
        var referenced = new HashSet<SqlCommonTableExpression>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<(SqlQueryExpression Query, CommonTableScope? Scope, int Visible)>();
        pending.Push((statement, null, 0));
        while (pending.TryPop(out var item))
        {
            var scope = item.Scope;
            int visible = item.Visible;
            var definitions = CommonTablesOf(item.Query);
            if (definitions.Count > 0)
            {
                scope = new CommonTableScope(scope, visible, definitions);
                visible = definitions.Count;
            }
            switch (item.Query)
            {
                case SqlSetOperationExpression set:
                    pending.Push((set.Left, scope, visible));
                    pending.Push((set.Right, scope, visible));
                    break;
                case SqlSelectExpression select:
                    foreach (var from in select.From)
                    {
                        Relation(from.Table, scope, visible);
                        foreach (var join in from.Joins) { Relation(join.Table, scope, visible); }
                    }
                    foreach (var expression in SubquerySources(select)) { Subquery(expression, scope, visible); }
                    break;
                case SqlInsertExpression insert:
                    if (insert.SelectSource is { } source) { pending.Push((source, scope, visible)); }
                    break;
                case SqlUpdateExpression update:
                    Sources(update.From, scope, visible);
                    Expressions(update.Where, scope, visible);
                    foreach (var assignment in update.Assignments) { Expressions(assignment.Value, scope, visible); }
                    break;
                case SqlDeleteExpression delete:
                    Sources(delete.Using, scope, visible);
                    Expressions(delete.Where, scope, visible);
                    break;
            }
        }
        return referenced;

        void Sources(IReadOnlyList<SqlFromItem> sources, CommonTableScope? scope, int visible)
        {
            foreach (var item in sources)
            {
                Relation(item.Table, scope, visible);
                foreach (var join in item.Joins)
                {
                    Relation(join.Table, scope, visible);
                    Expressions(join.Condition, scope, visible);
                }
            }
        }

        void Relation(SqlRelationReference relation, CommonTableScope? scope, int visible)
        {
            if (relation is SqlJoinedTableReference group)
            {
                System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
                Sources([group.Item], scope, visible);
            }
            else if (relation is SqlDerivedTableReference derived) { pending.Push((derived.Query, scope, visible)); }
            else if (relation is SqlTableReference { SchemaName: null } named && scope is not null &&
                scope.TryResolve(named.TableName, visible, out var owner, out int index))
            {
                var definition = owner.Definitions[index];
                if (referenced.Add(definition)) { pending.Push((definition.Body, owner, index)); }
            }
        }

        void Expressions(SqlExpression? expression, CommonTableScope? scope, int visible)
        {
            if (expression is null) { return; }
            foreach (var node in WalkSubqueries(expression)) { Subquery(node, scope, visible); }
        }

        void Subquery(SqlExpression expression, CommonTableScope? scope, int visible)
        {
            SqlQueryBodyExpression? child = expression switch
            {
                SqlSubqueryExpression scalar => scalar.Select,
                SqlExistsExpression exists => exists.Subquery,
                SqlInExpression member => member.Subquery,
                SqlQuantifiedComparisonExpression quantified => quantified.Subquery,
                _ => null,
            };
            if (child is not null) { pending.Push((child, scope, visible)); }
        }
    }

    [global::System.Diagnostics.DebuggerDisplay("CommonTableScope")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlPlanner.CommonTableScope.DebugView))]
    private sealed class CommonTableScope
    {
        private readonly CommonTableScope? _parent;
        private readonly int _parentVisible;
        private readonly Dictionary<string, int> _names;

        internal CommonTableScope(CommonTableScope? parent, int parentVisible, IReadOnlyList<SqlCommonTableExpression> definitions)
        {
            _parent = parent;
            _parentVisible = parentVisible;
            Definitions = definitions;
            _names = new(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < definitions.Count; index++) { _names.TryAdd(definitions[index].Name, index); }
        }

        internal IReadOnlyList<SqlCommonTableExpression> Definitions { get; }

        internal bool TryResolve(string name, int visible, out CommonTableScope owner, out int index)
        {
            CommonTableScope? current = this;
            while (current is not null)
            {
                if (current._names.TryGetValue(name, out index) && index < visible)
                {
                    owner = current;
                    return true;
                }
                visible = current._parentVisible;
                current = current._parent;
            }
            owner = null!;
            index = -1;
            return false;
        }

        private sealed class DebugView
        {
            public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlPlanner.CommonTableScope value)
            {
                _ = value;
            }
        }
    }
}
