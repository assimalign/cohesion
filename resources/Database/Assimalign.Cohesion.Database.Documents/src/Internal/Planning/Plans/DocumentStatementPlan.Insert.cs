using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed record DocumentInsertPlan(DocumentCollectionMetadata Collection, OqlInsertExpression Statement) : DocumentStatementPlan;
