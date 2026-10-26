using System.Collections.Generic;
using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed record DocumentUpdatePlan(DocumentCollectionMetadata Collection, OqlUpdateExpression Statement,
    IReadOnlyList<DocumentUpdateTarget> Targets, DocumentAccessPath Access, DocumentLogicalPlan? Returning = null) : DocumentStatementPlan;
