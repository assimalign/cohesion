using System.Collections.Generic;

using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed record DocumentJoinPlan(DocumentStatementPlan Input, DocumentLogicalPlan Logical,
    IReadOnlyDictionary<OqlJoinExpression, DocumentCollectionMetadata> Collections) : DocumentStatementPlan;
