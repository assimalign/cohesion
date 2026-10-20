using Assimalign.Cohesion.Database.Documents.Catalog;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed record DocumentDropIndexPlan(DocumentCollectionMetadata Collection, string IndexName, bool IfExists) : DocumentStatementPlan;
