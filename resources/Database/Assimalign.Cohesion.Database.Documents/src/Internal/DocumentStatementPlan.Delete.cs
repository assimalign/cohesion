using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed record DocumentDeletePlan(DocumentCollectionMetadata Collection, OqlDeleteExpression Statement, DocumentAccessPath Access) : DocumentStatementPlan;
