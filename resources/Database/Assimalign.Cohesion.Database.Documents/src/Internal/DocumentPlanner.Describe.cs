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
        DocumentDeletePlan => "Delete",
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
            DocumentDeletePlan p => p.Collection.Name,
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
            : logical is null ? null : $"filter={logical.Query.Predicate is not null}; grouped={logical.IsGrouped}; order_keys={logical.Query.OrderBy.Count}";
        Add(DescribeOperator(plan), null, indexName, detail);
        if (plan is DocumentDeletePlan deletion) { AddAccess(deletion.Access, 0); }
        var select = plan as DocumentPlan;
        int sourceParent = 0;
        if (plan is DocumentInsertSelectPlan insert)
        {
            sourceParent = 1;
            if (insert.Source is DocumentPlan source)
            {
                select = source;
                target = source.Collection.Name;
                Add("Select", 0);
            }
            else if (insert.Source is DocumentSystemCollectionPlan system)
            {
                target = system.Name;
                Add("SystemCollection", 0);
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
            AddAccess(select.Access, projectionId);
        }
        return DatabasePlanDescription.Create(nodes, diagnostics);

        void AddAccess(DocumentAccessPath access, int parent)
        {
            switch (access)
            {
                case DocumentScanPath:
                    Add("CollectionScan", parent);
                    break;
                case DocumentIndexPath seek:
                    var bounds = new List<string>();
                    if (seek.Lower is { } lower) { bounds.Add($"{seek.Index.Path} {(lower.Inclusive ? ">=" : ">")} {Value(lower.Value)}"); }
                    if (seek.Upper is { } upper) { bounds.Add($"{seek.Index.Path} {(upper.Inclusive ? "<=" : "<")} {Value(upper.Value)}"); }
                    Add("IndexSeek", parent, seek.Index.Name, string.Join(" AND ", bounds));
                    break;
                default:
                    throw new UnreachableException();
            }
        }

        void Add(string name, int? parent, string? index = null, string? detail = null)
            => nodes.Add(new DatabasePlanNode { NodeId = nodes.Count, ParentId = parent, Operator = name, Target = target, Index = index, Detail = detail });
    }

    private static string Value(object value) => value is string text
        ? "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'"
        : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL";
}
