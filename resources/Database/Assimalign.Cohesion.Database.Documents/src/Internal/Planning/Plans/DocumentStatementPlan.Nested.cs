using System.Collections.Generic;

using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed record DocumentNestedPlan(DocumentStatementPlan Root,
    IReadOnlyDictionary<OqlSubqueryExpression, DocumentNestedQueryPlan> Queries) : DocumentStatementPlan;

internal sealed record DocumentNestedQueryPlan(DocumentStatementPlan Plan, bool CacheForStatement, string Owner);
