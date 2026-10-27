using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed record DocumentInsertSelectPlan(DocumentCollectionMetadata Collection, OqlInsertExpression Statement,
    DocumentStatementPlan Source, int KeyOrdinal, int? ValueOrdinal, DocumentLogicalPlan? Returning = null) : DocumentStatementPlan;
