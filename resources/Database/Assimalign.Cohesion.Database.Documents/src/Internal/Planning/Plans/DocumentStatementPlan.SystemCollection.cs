namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed record DocumentSystemCollectionPlan(DocumentLogicalPlan Logical, string Name) : DocumentStatementPlan;
