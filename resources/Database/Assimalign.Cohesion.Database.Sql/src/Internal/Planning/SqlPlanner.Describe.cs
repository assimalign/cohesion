using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Indexing;
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
        SqlSequenceScopePlan => "Sequences",
        SqlSelectPlan => "Select",
        SqlSetOperationPlan p => p.Operator.ToString(),
        SqlJoinPlan { Steps.Count: 0 } => "Select",
        SqlJoinPlan { Steps: { } steps } when steps.Any(step => step.Hash is not null) => "HashJoin",
        SqlJoinPlan => "NestedLoopJoin",
        SqlSystemViewPlan => "SystemView",
        SqlGroupPlan => "Group",
        SqlSubqueryPlan => "Subqueries",
        SqlWithPlan => "With",
        SqlConstantPlan => "Constant",
        SqlCorrelatedSubqueryPlan => "CorrelatedSubqueries",
        SqlInsertSelectPlan => "InsertSelect",
        SqlInsertPlan => "Insert",
        SqlMergePlan => "Merge",
        SqlUpdatePlan => "Update",
        SqlDeletePlan => "Delete",
        SqlCreateTablePlan => "CreateTable",
        SqlCreateTableAsPlan => "CreateTableAs",
        SqlCreateSchemaPlan => "CreateSchema",
        SqlSequencePlan { Expression: SqlCreateSequenceExpression } => "CreateSequence",
        SqlSequencePlan { Expression: SqlAlterSequenceExpression } => "AlterSequence",
        SqlSequencePlan => "DropSequence",
        SqlCreateViewPlan => "CreateView",
        SqlDropViewPlan => "DropView",
        SqlDropTablePlan => "DropTable",
        SqlTruncatePlan => "Truncate",
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

    private static string SequencePlanTarget(SqlQueryExpression expression)
        => expression switch
        {
            SqlCreateSequenceExpression p => (p.Sequence.SchemaName ?? DefaultSchema) + "." + p.Sequence.TableName,
            SqlAlterSequenceExpression p => (p.Sequence.SchemaName ?? DefaultSchema) + "." + p.Sequence.TableName,
            SqlDropSequenceExpression p => string.Join(", ", p.Sequences.Select(s => (s.SchemaName ?? DefaultSchema) + "." + s.TableName)),
            _ => "",
        };

    internal static DatabasePlanDescription Describe(SqlPlan plan, IEnumerable<Diagnostic>? diagnostics = null, SqlStatementMetrics? metrics = null)
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
                SqlMergePlan p => Target(p.Table),
                SqlInsertSelectPlan p => Target(p.Table),
                SqlUpdatePlan p => Target(p.Table),
                SqlDeletePlan p => Target(p.Table),
                SqlCreateTablePlan p => p.Schema + "." + p.Name,
                SqlCreateTableAsPlan p => p.Schema + "." + p.Name,
                SqlCreateSchemaPlan p => p.Name,
                SqlSequencePlan p => SequencePlanTarget(p.Expression),
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
                SqlJoinPlan { Steps.Count: 0 } p => QueryDetail(p.Where is not null, p.OrderBy, p.IsDistinct, p.Limit, p.Offset),
                SqlConstantPlan p => QueryDetail(p.Where is not null, p.OrderBy, p.IsDistinct, p.Limit, p.Offset),
                SqlSetOperationPlan p => QueryDetail(false, p.OrderBy, !p.IsAll, p.Limit, p.Offset),
                SqlSelectPlan p => QueryDetail(p.Where is not null, p.OrderBy, p.IsDistinct, p.Limit, p.Offset),
                SqlSystemViewPlan p => QueryDetail(p.Where is not null, p.OrderBy, p.IsDistinct, p.Limit, p.Offset),
                SqlJoinPlan p => string.Join("; ", p.Steps.Select(step => $"{step.Kind}: {(step.Hash is not null ? "bounded in-memory hash" : step.Access is null ? "buffered inputs" : "correlated equality-prefix seek")}")) + "; " +
                    QueryDetail(p.Where is not null, p.OrderBy, p.IsDistinct, p.Limit, p.Offset),
                SqlGroupPlan p => $"keys={p.Keys.Count}; aggregates={p.Aggregates.Count}; having={p.Having is not null}; " +
                    (p.Aggregates.Any(aggregate => aggregate.Filter is not null) ? $"filtered_aggregates={p.Aggregates.Count(aggregate => aggregate.Filter is not null)}; " : "") +
                    QueryDetail(false, p.OrderBy, p.IsDistinct, p.Limit, p.Offset),
                SqlInsertPlan p => DmlDetail($"values rows={p.Rows.Count}", p.Returning),
                SqlInsertSelectPlan p => DmlDetail(null, p.Returning),
                SqlMergePlan p => DmlDetail($"join={p.Source.Associations.Tree!.Step!.Kind}; actions=written_order", p.Returning),
                SqlUpdatePlan p => DmlDetail($"assignments={p.Assignments.Count}; filter={p.Where is not null}", p.Returning),
                SqlDeletePlan p => DmlDetail($"filter={p.Where is not null}", p.Returning),
                SqlDefinitionPlan p => p.Expression.CommandType.ToString(),
                SqlSequencePlan p => p.Expression switch
                {
                    SqlCreateSequenceExpression create => SqlExpressionRenderer.Render(create),
                    SqlAlterSequenceExpression alter => SqlExpressionRenderer.Render(alter),
                    SqlDropSequenceExpression drop => SqlExpressionRenderer.Render(drop),
                    _ => null,
                },
                _ => null,
            };
            if (current.ContainsSequences) { detail = (detail is null ? "" : detail + "; ") + "sequences=volatile; object_locks=transaction"; }
            string? indexName = current switch
            {
                SqlCreateIndexPlan p => p.IndexName,
                SqlDropIndexPlan p => p.IndexName,
                _ => null,
            };
            Add(DescribeOperator(current), parent, target, indexName, detail, actual: metrics?.ActualCandidates(current));
            switch (current)
            {
                case SqlSequenceScopePlan sequence:
                    Visit(sequence.Input, id);
                    break;
                case SqlSetOperationPlan set:
                    foreach (var operand in set.Operands) { Visit(operand.Query, id); }
                    break;
                case SqlSelectPlan select:
                    Access(select.Table, select.Access, id, actual: metrics?.ActualCandidates(select));
                    break;
                case SqlJoinPlan join:
                    if (join.Tree is { } root)
                    {
                        var pending = new Stack<(SqlJoinTree Node, int Parent, bool AfterOuter)>();
                        pending.Push((root, id, false));
                        while (pending.TryPop(out var next))
                        {
                            if (next.Node.Step is not { } treeStep)
                            {
                                JoinInput(next.Node.Binding, next.Parent);
                                continue;
                            }
                            if (next.AfterOuter)
                            {
                                var treeSeek = treeStep.Access!;
                                string bounds = string.Join(" AND ", treeSeek.InnerColumns.Select((column, i) =>
                                    $"{column.Name} = {JoinProbeReference(join.Bindings, treeSeek.OuterOrdinals[i])}"));
                                Add("IndexSeek", next.Parent, Target(join.Bindings[treeSeek.InnerBinding].Source), treeSeek.Index.Name, bounds);
                                continue;
                            }
                            int treeId = nodes.Count;
                            string algorithm = treeStep.Kind == SqlJoinType.FullOuter ? "FullHashJoin; unmatched=both"
                                : treeStep.Hash is not null ? "HashJoin" : treeStep.Access is not null ? "IndexNestedLoop" : "NestedLoop";
                            string normalization = treeStep.Kind == SqlJoinType.RightOuter ? "; normalized RIGHT to LEFT; physical=LeftOuter" : string.Empty;
                            Add("JoinStep", next.Parent, Target(join.Bindings[treeStep.Binding].Source),
                                detail: $"{treeStep.Kind}; {algorithm}{normalization}; hash_budget=no_spill",
                                actual: metrics?.ActualCandidates(treeStep));
                            if (treeStep.Access is not null)
                            {
                                // Emit the inner probe only after the complete physical outer subtree.
                                pending.Push((next.Node, treeId, true));
                            }
                            else { pending.Push((next.Node.Right!, treeId, false)); }
                            pending.Push((next.Node.Left!, treeId, false));
                        }
                        break;
                    }
                    int first = join.FirstBinding;
                    JoinInput(first, id);
                    foreach (var step in join.Steps)
                    {
                        int inner = ReferenceEquals(step, join.Steps[0]) && first == 1 ? 0 : step.Binding;
                        var binding = join.Bindings[inner];
                        int stepId = nodes.Count;
                        string algorithm = step.Hash is not null ? "HashJoin" : step.Access is not null ? "IndexNestedLoop" : "NestedLoop";
                        long? estimated = null;
                        if (ReferenceEquals(step, join.Steps[0]) && join.InputEstimates is { } counts &&
                            counts[first] is { } outerCount && counts[inner] is { } innerCount)
                        {
                            estimated = step.Hash is not null ? Math.Min(outerCount.Count, innerCount.Count)
                                : outerCount.Count * innerCount.Count;
                        }
                        Add("JoinStep", id, Target(join.Bindings[step.Binding].Source),
                            detail: $"{step.Kind}; {algorithm}; estimate_kind=heuristic; hash_budget=no_spill",
                            estimated: estimated, actual: metrics?.ActualCandidates(step));
                        if (step.Access is { } seek)
                        {
                            string bounds = string.Join(" AND ", seek.InnerColumns.Select((column, i) =>
                                $"{column.Name} = {JoinProbeReference(join.Bindings, seek.OuterOrdinals[i])}"));
                            Add("IndexSeek", stepId, Target(binding.Source), seek.Index.Name, bounds);
                        }
                        else { JoinInput(inner, stepId); }
                    }
                    break;

                    void JoinInput(int binding, int parentId)
                    {
                        var source = join.Bindings[binding].Source;
                        if (source.Table is { } table && join.Inputs.Count > 0)
                        { Access(table, join.Inputs[binding].Access, parentId, join.InputEstimates?[binding], metrics?.ActualCandidates(join.Bindings[binding])); }
                        else { RelationScan(source, parentId); }
                    }
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
                        using var analysis = metrics?.EnterRelationAnalysis(relation);
                        Visit(relation.Query, materialization);
                    }
                    Visit(with.Input, id);
                    break;
                case SqlInsertSelectPlan insert:
                    Visit(insert.Source, id);
                    break;
                case SqlMergePlan merge:
                    Visit(merge.Source.Associations, id);
                    for (int actionIndex = 0; actionIndex < merge.Actions.Count; actionIndex++)
                    {
                        var action = merge.Actions[actionIndex];
                        int actionId = nodes.Count;
                        Add("MergeAction", id, Target(merge.Table), detail: $"order={actionIndex + 1}; {action.MatchKind}; {action.Action}; condition={action.Condition is not null}; assignments={action.Assignments.Count}; insert_columns={action.InsertOrdinals.Count}");
                        foreach (var query in action.Queries) { Visit(query.Query, actionId); }
                    }
                    break;
                case SqlUpdatePlan update:
                    if (update.Source is { } updateSource) { Visit(updateSource.Associations, id); }
                    else { Access(update.Table, update.Access, id); }
                    break;
                case SqlDeletePlan delete:
                    if (delete.Source is { } deleteSource) { Visit(deleteSource.Associations, id); }
                    else { Access(delete.Table, delete.Access, id); }
                    break;
            }
        }

        static string? DmlDetail(string? detail, IReadOnlyList<SqlProjection>? returning)
            => returning is null ? detail :
                $"{(detail is null ? string.Empty : detail + "; ")}returning={returning.Count}; columns={string.Join(", ", returning.Select(column => column.Name))}";

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
                Add(derived.CatalogView is null ? "DerivedTable" : "View", parent, Target(source));
                using var analysis = metrics?.EnterRelationAnalysis(derived);
                Visit(derived.Query, id);
            }
            else { Add(source.View is not null ? "SystemView" : "TableScan", parent, Target(source)); }
        }

        void Access(SqlCatalogTable table, SqlAccessPath access, int parent, BTreeRangeCount? estimate = null, long? actual = null)
        {
            estimate ??= access switch { SqlIndexSeekPath seekPath => seekPath.Cost?.Estimate, SqlScanPath scanPath => scanPath.Cost?.Estimate, _ => null };
            string? probe = estimate is { } count ?
                $"estimate_kind={(count.IsComplete ? "exact_probe" : "lower_bound")}; probe_entries={count.EntriesExamined}; probe_pages={count.PagesVisited}" : null;
            if (access is SqlScanPath) { Add("TableScan", parent, Target(table), detail: probe, estimated: estimate?.Count, actual: actual); return; }
            if (access is not SqlIndexSeekPath seek) { throw new UnreachableException(); }
            var bounds = new List<string>();
            for (int i = 0; i < seek.EqualityValues.Count; i++)
            { bounds.Add($"{seek.Index.ColumnNames[i]} = {Value(seek.EqualityValues[i])}"); }
            int next = seek.EqualityValues.Count;
            if (seek.Lower is { } lower) { bounds.Add($"{seek.Index.ColumnNames[next]} {(lower.Inclusive ? ">=" : ">")} {Value(lower.Value)}"); }
            if (seek.Upper is { } upper) { bounds.Add($"{seek.Index.ColumnNames[next]} {(upper.Inclusive ? "<=" : "<")} {Value(upper.Value)}"); }
            Add("IndexSeek", parent, Target(table), seek.Index.Name,
                string.Join(" AND ", bounds) + (probe is null ? string.Empty : "; " + probe),
                estimated: estimate?.Count ?? (seek.Index.IsUnique && seek.EqualityValues.Count == seek.Index.ColumnNames.Count ? 1 : null), actual: actual);
        }

        void Add(string name, int? parent, string? target = null, string? index = null, string? detail = null, long? estimated = null, long? actual = null)
            => nodes.Add(new DatabasePlanNode { NodeId = nodes.Count, ParentId = parent, Operator = name, Target = target, Index = index, Detail = detail, EstimatedRows = estimated, ActualRows = actual });
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

    private static string QueryDetail(bool filter, IReadOnlyList<SqlBoundOrdering> orderBy, bool distinct, SqlRowCount? limit, SqlRowCount? offset)
    {
        string ordering = string.Join(", ", orderBy.Select(key =>
            $"{(key.IsDescending ? "DESC" : "ASC")} NULLS {(key.NullsFirst ? "FIRST" : "LAST")}"));
        return FormattableString.Invariant($"filter={filter}; order_keys={orderBy.Count}; order_by=[{ordering}]; distinct={distinct}; limit={limit}; offset={offset}");
    }
}
