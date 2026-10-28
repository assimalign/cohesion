using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

using Assimalign.Cohesion.Database.Documents.Language;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed partial class DocumentPlanner
{
    internal static string DescribeOperator(object plan) => plan switch
    {
        DocumentPlan => "Select",
        DocumentLogicalPlan { Query.IsValue: true, IsGrouped: false } => "ProjectValue",
        DocumentLogicalPlan { IsGrouped: true } => "Group",
        DocumentLogicalPlan => "Project",
        DocumentSystemCollectionPlan => "SystemCollection",
        DocumentInsertPlan => "Insert",
        DocumentUpsertPlan { Source: not null } => "UpsertSelect",
        DocumentUpsertPlan => "Upsert",
        DocumentDeletePlan => "Delete",
        DocumentUpdatePlan => "Update",
        DocumentInsertSelectPlan => "InsertSelect",
        DocumentCreateCollectionPlan => "CreateCollection",
        DocumentDropCollectionPlan => "DropCollection",
        DocumentCreateIndexPlan => "CreateIndex",
        DocumentDropIndexPlan => "DropIndex",
        _ => throw new UnreachableException(),
    };

    internal static DatabasePlanDescription Describe(DocumentStatementPlan plan, IEnumerable<Diagnostic>? diagnostics = null)
    {
        var nodes = new List<DatabasePlanNode>();
        string? target = plan switch
        {
            DocumentPlan p => p.Collection.Name,
            DocumentSystemCollectionPlan p => p.Name,
            DocumentInsertPlan p => p.Collection.Name,
            DocumentUpsertPlan p => p.Collection.Name,
            DocumentDeletePlan p => p.Collection.Name,
            DocumentUpdatePlan p => p.Collection.Name,
            DocumentInsertSelectPlan p => p.Collection.Name,
            DocumentCreateCollectionPlan p => p.Statement.Name,
            DocumentDropCollectionPlan p => p.Statement.Name,
            DocumentCreateIndexPlan p => p.Collection.Name,
            DocumentDropIndexPlan p => p.Collection.Name,
            _ => null,
        };
        var logical = plan switch
        {
            DocumentPlan p => p.Logical,
            DocumentSystemCollectionPlan p => p.Logical,
            _ => null,
        };
        string? indexName = plan switch
        {
            DocumentCreateIndexPlan p => p.IndexName,
            DocumentDropIndexPlan p => p.IndexName,
            _ => null,
        };
        string? detail = plan is DocumentDeletePlan delete
            ? $"filter={delete.Statement.Predicate is not null}; access={AccessName(delete.Access)}"
            : plan is DocumentUpdatePlan update
            ? $"filter={update.Statement.Predicate is not null}; access={AccessName(update.Access)}"
            : logical is null ? null : SelectDetail(logical);
        if (plan is DocumentSystemCollectionPlan && logical is { Query.Unnest.Count: 0 } &&
            AggregateDetail(logical) is { } aggregates)
        { detail += "; " + aggregates; }
        Add(DescribeOperator(plan), null, indexName, detail);
        var returning = plan switch
        {
            DocumentInsertPlan p => p.Returning,
            DocumentInsertSelectPlan p => p.Returning,
            DocumentUpsertPlan p => p.Returning,
            DocumentUpdatePlan p => p.Returning,
            DocumentDeletePlan p => p.Returning,
            _ => null,
        };
        if (returning is not null)
        {
            Add(returning.Query.IsValue ? "ReturningValue" : "Returning", 0,
                detail: $"columns={returning.Projections.Count}");
        }
        if (plan is DocumentDeletePlan deletion) { AddAccess(deletion.Access, 0); }
        if (plan is DocumentUpdatePlan mutation) { AddAccess(mutation.Access, 0); }
        var select = plan as DocumentPlan;
        var systemSelect = plan as DocumentSystemCollectionPlan;
        int sourceParent = 0;
        var writeSource = plan switch
        {
            DocumentInsertSelectPlan insert => insert.Source,
            DocumentUpsertPlan upsert => upsert.Source,
            _ => null,
        };
        if (writeSource is not null)
        {
            sourceParent = nodes.Count;
            if (writeSource is DocumentPlan source)
            {
                select = source;
                target = source.Collection.Name;
                Add("Select", 0, detail: SelectDetail(source.Logical));
            }
            else if (writeSource is DocumentSystemCollectionPlan system)
            {
                systemSelect = system;
                target = system.Name;
                Add("SystemCollection", 0, detail: SelectDetail(system.Logical));
            }
        }
        if (select is not null)
        {
            if (select.Logical.Query.IsDistinct)
            {
                int distinctId = nodes.Count;
                Add("Distinct", sourceParent);
                sourceParent = distinctId;
            }
            int projectionId = nodes.Count;
            Add(DescribeOperator(select.Logical), sourceParent, detail: AggregateDetail(select.Logical));
            projectionId = AddBindings(select.Logical, projectionId);
            AddAccess(select.Access, projectionId);
        }
        if (systemSelect is not null && systemSelect.Logical.Query.Unnest.Count > 0)
        {
            int parent = sourceParent;
            if (systemSelect.Logical.Query.IsDistinct)
            {
                parent = nodes.Count;
                Add("Distinct", sourceParent);
            }
            int projectionId = nodes.Count;
            Add(DescribeOperator(systemSelect.Logical), parent, detail: AggregateDetail(systemSelect.Logical));
            Add("SystemCollectionScan", AddBindings(systemSelect.Logical, projectionId));
        }
        return DatabasePlanDescription.Create(nodes, diagnostics);

        int AddBindings(DocumentLogicalPlan sourceLogical, int parent)
        {
            // Execution reads this chain from access upwards, preserving binding order.
            for (int i = sourceLogical.Query.Unnest.Count - 1; i >= 0; i--)
            {
                var binding = sourceLogical.Query.Unnest[i];
                int bindingId = nodes.Count;
                Add(binding.IsLeft ? "LeftUnnest" : "Unnest", parent, detail: binding.Variable);
                parent = bindingId;
            }
            return parent;
        }

        void AddAccess(DocumentAccessPath access, int parent)
        {
            switch (access)
            {
                case DocumentScanPath:
                    Add("CollectionScan", parent);
                    break;
                case DocumentIndexPath seek:
                    var bounds = new List<string>();
                    var prefix = seek.EqualityPrefix ?? [];
                    for (int i = 0; i < prefix.Count; i++) { bounds.Add($"{DocumentIndexRendering.Key(seek.Index.Keys[i])} = {Value(prefix[i])}"); }
                    string rangePath = DocumentIndexRendering.Key(seek.Index.Keys[Math.Min(prefix.Count, seek.Index.Keys.Count - 1)]);
                    if (seek.Lower is { } lower) { bounds.Add($"{rangePath} {(lower.Inclusive ? ">=" : ">")} {Value(lower.Value)}"); }
                    if (seek.Upper is { } upper) { bounds.Add($"{rangePath} {(upper.Inclusive ? "<=" : "<")} {Value(upper.Value)}"); }
                    string description = string.Join(" AND ", bounds);
                    if (System.Linq.Enumerable.Any(seek.Index.Keys, key => key.ArraySource is not null))
                    { description += "; distinct_documents=True; array_residual=True"; }
                    if (seek.Estimate is { } estimate)
                    {
                        description += $"; estimate_kind={(estimate.IsComplete ? "exact_index_entries" : "lower_bound")}; probe_entries={estimate.EntriesExamined}; probe_pages={estimate.PagesVisited}";
                    }
                    Add("IndexSeek", parent, seek.Index.Name, description, seek.Estimate?.Count);
                    break;
                case DocumentIntersectionPath intersection:
                    int id = nodes.Count;
                    Add("IndexIntersection", parent, detail: $"inputs={intersection.Inputs.Count}; estimate_kind=upper_bound",
                        estimated: System.Linq.Enumerable.Min(intersection.Inputs, path => path.Estimate!.Value.Count));
                    foreach (var input in intersection.Inputs) { AddAccess(input, id); }
                    break;
                default:
                    throw new UnreachableException();
            }
        }

        void Add(string name, int? parent, string? index = null, string? detail = null, long? estimated = null)
            => nodes.Add(new DatabasePlanNode { NodeId = nodes.Count, ParentId = parent, Operator = name, Target = target,
                Index = index, Detail = detail, EstimatedRows = estimated });
    }

    private static string? AggregateDetail(DocumentLogicalPlan logical)
    {
        var calls = new List<string>();
        foreach (var projection in logical.Projections) { Visit(projection.Expression); }
        if (logical.Query.Having is { } having) { Visit(having); }
        foreach (var order in logical.Query.OrderBy) { Visit(order.Expression); }
        string? detail = calls.Count == 0 ? null : "aggregates=" + string.Join(", ", calls);
        if (logical.Query.GroupAs is string groupAs) { detail = Append(detail, "group_as=" + groupAs); }
        if (logical.Query.GroupAliases.Any(name => name is not null))
        { detail = Append(detail, "group_aliases=" + string.Join(", ", logical.Query.GroupAliases.Select(name => name ?? "<unnamed>"))); }
        return detail;

        static string Append(string? detail, string value) => detail is null ? value : detail + "; " + value;

        void Visit(OqlExpression expression)
        {
            if (expression is OqlCallExpression call)
            {
                if (call.Quantifier != OqlAggregateQuantifier.None || OqlFunctions.Find(call.Name)?.IsAggregate == true)
                {
                    calls.Add(call.Name + "(" + (call.Quantifier == OqlAggregateQuantifier.Distinct ? "DISTINCT" :
                        call.Arguments.Count == 1 && call.Arguments[0] is OqlStarExpression ? "*" : "ALL") + ")");
                }
                foreach (var argument in call.Arguments) { Visit(argument); }
            }
            else if (expression is OqlBinaryExpression binary) { Visit(binary.Left); Visit(binary.Right); }
            else if (expression is OqlUnaryExpression unary) { Visit(unary.Operand); }
            else if (expression is OqlArrayExpression array) { foreach (var element in array.Elements) { Visit(element); } }
            else if (expression is OqlObjectExpression obj) { foreach (var member in obj.Members) { Visit(member.Value); } }
            else { foreach (var child in PredicateChildren(expression)) { Visit(child); } }
        }
    }

    private static string AccessName(DocumentAccessPath access) => access switch
    {
        DocumentIndexPath => "IndexSeek",
        DocumentIntersectionPath => "IndexIntersection",
        _ => "CollectionScan",
    };

    private static string SelectDetail(DocumentLogicalPlan logical)
        => $"filter={logical.Query.Predicate is not null}; grouped={logical.IsGrouped}; order_keys={logical.Query.OrderBy.Count}; limit={logical.Limit?.ToString(CultureInfo.InvariantCulture) ?? "none"}; offset={logical.Offset.ToString(CultureInfo.InvariantCulture)}";

    private static string Value(object value) => value is string text
        ? "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'"
        : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL";
}
