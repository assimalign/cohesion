using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

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
            ? $"filter={delete.Statement.Predicate is not null}; access={(delete.Access is DocumentIndexPath ? "IndexSeek" : "CollectionScan")}"
            : plan is DocumentUpdatePlan update
            ? $"filter={update.Statement.Predicate is not null}; access={(update.Access is DocumentIndexPath ? "IndexSeek" : "CollectionScan")}"
            : logical is null ? null : SelectDetail(logical);
        Add(DescribeOperator(plan), null, indexName, detail);
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
            sourceParent = 1;
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
            Add(DescribeOperator(select.Logical), sourceParent);
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
            Add(DescribeOperator(systemSelect.Logical), parent);
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
                    for (int i = 0; i < prefix.Count; i++) { bounds.Add($"{seek.Index.Paths[i]} = {Value(prefix[i])}"); }
                    string rangePath = seek.Index.Paths[Math.Min(prefix.Count, seek.Index.Paths.Count - 1)];
                    if (seek.Lower is { } lower) { bounds.Add($"{rangePath} {(lower.Inclusive ? ">=" : ">")} {Value(lower.Value)}"); }
                    if (seek.Upper is { } upper) { bounds.Add($"{rangePath} {(upper.Inclusive ? "<=" : "<")} {Value(upper.Value)}"); }
                    Add("IndexSeek", parent, seek.Index.Name, string.Join(" AND ", bounds));
                    break;
                default:
                    throw new UnreachableException();
            }
        }

        void Add(string name, int? parent, string? index = null, string? detail = null)
            => nodes.Add(new DatabasePlanNode { NodeId = nodes.Count, ParentId = parent, Operator = name, Target = target, Index = index, Detail = detail });
    }

    private static string SelectDetail(DocumentLogicalPlan logical)
        => $"filter={logical.Query.Predicate is not null}; grouped={logical.IsGrouped}; order_keys={logical.Query.OrderBy.Count}; limit={logical.Limit?.ToString(CultureInfo.InvariantCulture) ?? "none"}; offset={logical.Offset.ToString(CultureInfo.InvariantCulture)}";

    private static string Value(object value) => value is string text
        ? "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'"
        : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL";
}
