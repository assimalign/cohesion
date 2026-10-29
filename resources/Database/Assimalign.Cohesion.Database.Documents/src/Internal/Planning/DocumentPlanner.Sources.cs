using System.Collections.Generic;

using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed partial class DocumentPlanner
{
    private DocumentFromPlan PlanFrom(OqlSelectExpression query,
        IReadOnlyDictionary<OqlSubqueryExpression, DocumentNestedQueryPlan>? nested, IReadOnlySet<string>? outerNames = null)
    {
        if (string.IsNullOrEmpty(query.Alias))
        { throw new DocumentPlanningException("CD01033", ErrorMessages.FromSourceAliasRequired, query.Source!.Location); }
        Validate(query.Source!, allowAggregate: false, functions: _functions);
        ValidateMetadata(query.Source!, null, storedSource: false, outerNames);
        var logical = CreateLogicalPlan(query, _cancellationToken, _parameters, _functions, outerNames);
        // Expression rows have no physical order to consume. Their headers describe the
        // selected window; a dynamic path does not force reads beyond LIMIT.
        logical = logical with { CanStopEarly = !logical.IsGrouped && !query.IsDistinct && query.OrderBy.Count == 0 };
        return new DocumentFromPlan(logical, query.Source is OqlSubqueryExpression derived
            ? nested is not null ? nested[derived].Plan : PlanCore(derived.Query) : null);
    }

    private static DocumentLogicalPlan QueryLogical(DocumentStatementPlan plan) => plan switch
    {
        DocumentPlan select => select.Logical,
        DocumentFromPlan source => source.Logical,
        DocumentSystemCollectionPlan system => system.Logical,
        _ => throw new DocumentPlanningException("CD05001", ErrorMessages.DocumentPlannerPlanFailure),
    };
}
