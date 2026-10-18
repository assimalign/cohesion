using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    // The same closed mapping supplies every emitted plan node. Tests enumerate the plan
    // family and exercise this switch, so a new record cannot silently disappear from EXPLAIN.
    internal static string DescribeOperator(SqlPlan plan) => plan switch
    {
        SqlSelectPlan => "Select",
        SqlSetOperationPlan p => p.Operator.ToString(),
        SqlJoinPlan { Steps.Count: 0 } => "Select",
        SqlJoinPlan => "NestedLoopJoin",
        SqlSystemViewPlan => "SystemView",
        SqlGroupPlan => "Group",
        SqlSubqueryPlan => "Subqueries",
        SqlWithPlan => "With",
        SqlConstantPlan => "Constant",
        SqlCorrelatedSubqueryPlan => "CorrelatedSubqueries",
        SqlInsertSelectPlan => "InsertSelect",
        SqlInsertPlan => "Insert",
        SqlUpdatePlan => "Update",
        SqlDeletePlan => "Delete",
        SqlCreateTablePlan => "CreateTable",
        SqlCreateSchemaPlan => "CreateSchema",
        SqlDropTablePlan => "DropTable",
        SqlDropSchemaPlan => "DropSchema",
        SqlAddColumnPlan => "AddColumn",
        SqlRenamePlan => "Rename",
        SqlAlterColumnPlan => "AlterColumn",
        SqlDropColumnPlan => "DropColumn",
        SqlAddConstraintPlan => "AddConstraint",
        SqlDropConstraintPlan => "DropConstraint",
        SqlCreateIndexPlan => "CreateIndex",
        SqlDropIndexPlan => "DropIndex",
        SqlDefinitionPlan => "Definition",
        _ => throw new UnreachableException(),
    };

    internal static DatabasePlanDescription Describe(SqlPlan plan, IEnumerable<Diagnostic>? diagnostics = null)
    {
        var nodes = new List<DatabasePlanNode>();
        Visit(plan, null);
        return DatabasePlanDescription.Create(nodes, diagnostics);

        void Visit(SqlPlan current, int? parent)
        {
            System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
            int id = nodes.Count;
            string? target = current switch
            {
                SqlSelectPlan p => Target(p.Table),
                SqlSystemViewPlan p => p.View.Schema + "." + p.View.Name,
                SqlInsertPlan p => Target(p.Table),
                SqlInsertSelectPlan p => Target(p.Table),
                SqlUpdatePlan p => Target(p.Table),
                SqlDeletePlan p => Target(p.Table),
                SqlCreateTablePlan p => p.Schema + "." + p.Name,
                SqlCreateSchemaPlan p => p.Name,
                SqlDropTablePlan p => p.Schema + "." + p.Name,
                SqlDropSchemaPlan p => p.Name,
                SqlAddColumnPlan p => p.Schema + "." + p.Name,
                SqlDropColumnPlan p => p.Schema + "." + p.Name,
                SqlRenamePlan p => Target(p.Table),
                SqlAlterColumnPlan p => Target(p.Table),
                SqlAddConstraintPlan p => Target(p.Table),
                SqlDropConstraintPlan p => Target(p.Table),
                SqlCreateIndexPlan p => Target(p.Table),
                SqlDropIndexPlan p => Target(p.Table),
                SqlDefinitionPlan { Expression: SqlCreateFunctionExpression p } => p.Name,
                SqlDefinitionPlan { Expression: SqlDropFunctionExpression p } => p.Name,
                SqlDefinitionPlan { Expression: SqlCreateDomainExpression p } => p.Name,
                SqlDefinitionPlan { Expression: SqlDropDomainExpression p } => p.Name,
                _ => null,
            };
            string? detail = current switch
            {
                SqlJoinPlan { Steps.Count: 0 } p => QueryDetail(p.Where is not null, p.OrderBy.Count, p.IsDistinct, p.Limit, p.Offset),
                SqlConstantPlan p => QueryDetail(p.Where is not null, p.OrderBy.Count, p.IsDistinct, p.Limit, p.Offset),
                SqlSetOperationPlan p => QueryDetail(false, p.OrderBy.Count, !p.IsAll, p.Limit, p.Offset),
                SqlSelectPlan p => QueryDetail(p.Where is not null, p.OrderBy.Count, p.IsDistinct, p.Limit, p.Offset),
                SqlSystemViewPlan p => QueryDetail(p.Where is not null, p.OrderBy.Count, p.IsDistinct, p.Limit, p.Offset),
                SqlJoinPlan p => string.Join("; ", p.Steps.Select(step => $"{step.Kind}: {(step.Access is null ? "buffered inputs" : "correlated equality-prefix seek")}")) + "; " +
                    QueryDetail(p.Where is not null, p.OrderBy.Count, p.IsDistinct, p.Limit, p.Offset),
                SqlGroupPlan p => $"keys={p.Keys.Count}; aggregates={p.Aggregates.Count}; having={p.Having is not null}; " +
                    QueryDetail(false, p.OrderBy.Count, p.IsDistinct, p.Limit, p.Offset),
                SqlInsertPlan p => $"values rows={p.Rows.Count}",
                SqlUpdatePlan p => $"assignments={p.Assignments.Count}; filter={p.Where is not null}",
                SqlDeletePlan p => $"filter={p.Where is not null}",
                SqlDefinitionPlan p => p.Expression.CommandType.ToString(),
                _ => null,
            };
            string? indexName = current switch
            {
                SqlCreateIndexPlan p => p.IndexName,
                SqlDropIndexPlan p => p.IndexName,
                _ => null,
            };
            Add(DescribeOperator(current), parent, target, indexName, detail);
            switch (current)
            {
                case SqlSetOperationPlan set:
                    foreach (var operand in set.Operands) { Visit(operand.Query, id); }
                    break;
                case SqlSelectPlan select:
                    Access(select.Table, select.Access, id);
                    break;
                case SqlJoinPlan join:
                    bool swapped = join.Steps.Count > 0 && join.Steps[0].Access?.InnerBinding == 0;
                    RelationScan(join.Bindings[swapped ? 1 : 0].Source, id);
                    foreach (var step in join.Steps)
                    {
                        var binding = join.Bindings[step.Access?.InnerBinding ?? step.Binding];
                        int stepId = nodes.Count;
                        Add("JoinStep", id, Target(join.Bindings[step.Binding].Source), detail: step.Kind.ToString());
                        if (step.Access is { } seek)
                        {
                            string bounds = string.Join(" AND ", seek.InnerColumns.Select((column, i) =>
                                $"{column.Name} = {JoinProbeReference(join.Bindings, seek.OuterOrdinals[i])}"));
                            Add("IndexSeek", stepId, Target(binding.Source), seek.Index.Name, bounds);
                        }
                        else { RelationScan(binding.Source, stepId); }
                    }
                    break;
                case SqlGroupPlan group:
                    Visit(group.Input, id);
                    break;
                case SqlCorrelatedSubqueryPlan correlated:
                    foreach (var child in correlated.Queries) { Visit(child.Query, id); }
                    Visit(correlated.Input, id);
                    break;
                case SqlSubqueryPlan subqueries:
                    foreach (var child in subqueries.Queries) { Visit(child.Query, id); }
                    Visit(subqueries.Input, id);
                    break;
                case SqlWithPlan with:
                    foreach (var relation in with.Relations)
                    {
                        int materialization = nodes.Count;
                        Add("Materialize", id, relation.Name);
                        Visit(relation.Query, materialization);
                    }
                    Visit(with.Input, id);
                    break;
                case SqlInsertSelectPlan insert:
                    Visit(insert.Source, id);
                    break;
                case SqlUpdatePlan update:
                    Add("TableScan", id, Target(update.Table));
                    break;
                case SqlDeletePlan delete:
                    Add("TableScan", id, Target(delete.Table));
                    break;
            }
        }

        void RelationScan(SqlRelationSource source, int parent)
        {
            if (source.Derived is { IsCommonTable: true } commonTable)
            {
                Add("CteScan", parent, commonTable.Name);
                return;
            }
            if (source.Derived is { } derived)
            {
                int id = nodes.Count;
                Add("DerivedTable", parent, derived.Name);
                Visit(derived.Query, id);
            }
            else { Add(source.View is not null ? "SystemView" : "TableScan", parent, Target(source)); }
        }

        void Access(SqlCatalogTable table, SqlAccessPath access, int parent)
        {
            if (access is SqlScanPath) { Add("TableScan", parent, Target(table)); return; }
            if (access is not SqlIndexSeekPath seek) { throw new UnreachableException(); }
            var bounds = new List<string>();
            for (int i = 0; i < seek.EqualityValues.Count; i++)
            { bounds.Add($"{seek.Index.ColumnNames[i]} = {Value(seek.EqualityValues[i])}"); }
            int next = seek.EqualityValues.Count;
            if (seek.Lower is { } lower) { bounds.Add($"{seek.Index.ColumnNames[next]} {(lower.Inclusive ? ">=" : ">")} {Value(lower.Value)}"); }
            if (seek.Upper is { } upper) { bounds.Add($"{seek.Index.ColumnNames[next]} {(upper.Inclusive ? "<=" : "<")} {Value(upper.Value)}"); }
            Add("IndexSeek", parent, Target(table), seek.Index.Name, string.Join(" AND ", bounds));
        }

        void Add(string name, int? parent, string? target = null, string? index = null, string? detail = null)
            => nodes.Add(new DatabasePlanNode { NodeId = nodes.Count, ParentId = parent, Operator = name, Target = target, Index = index, Detail = detail });
    }

    private static string JoinProbeReference(IReadOnlyList<SqlRelationBinding> bindings, int ordinal)
    {
        var binding = bindings.Last(candidate => candidate.Offset <= ordinal);
        return (binding.Reference.Alias ?? binding.Source.Name) + "." + binding.Source.Columns[ordinal - binding.Offset].Name;
    }

    private static string Target(SqlRelationSource source) => source.Derived is not null ? source.Name : source.Schema + "." + source.Name;
    private static string Target(SqlCatalogTable table) => table.Schema + "." + table.Name;
    private static string Value(object? value) => value switch
    {
        null => "NULL",
        string text => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'",
        SqlCharacter character => Value(character.Value),
        byte[] bytes => "X'" + Convert.ToHexString(bytes) + "'",
        ReadOnlyMemory<byte> bytes => "X'" + Convert.ToHexString(bytes.Span) + "'",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL",
    };

    private static string QueryDetail(bool filter, int orderKeys, bool distinct, long? limit, long? offset)
        => FormattableString.Invariant($"filter={filter}; order_keys={orderKeys}; distinct={distinct}; limit={limit}; offset={offset}");
}
