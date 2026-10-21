using Assimalign.Cohesion.Database.Documents.Catalog;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed record DocumentPlan(DocumentLogicalPlan Logical, DocumentCollectionMetadata Collection, DocumentAccessPath Access) : DocumentStatementPlan;
