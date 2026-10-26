using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed record DocumentUpsertPlan(DocumentCollectionMetadata Collection, OqlUpsertExpression Statement,
    DocumentStatementPlan? Source = null, int KeyOrdinal = 0, int? ValueOrdinal = null, DocumentLogicalPlan? Returning = null) : DocumentStatementPlan;
