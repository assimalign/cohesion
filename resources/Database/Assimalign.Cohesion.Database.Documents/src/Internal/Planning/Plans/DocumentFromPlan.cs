using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

// A null input denotes the expression retained in Logical.Query.Source.
internal sealed record DocumentFromPlan(DocumentLogicalPlan Logical, DocumentStatementPlan? Input) : DocumentStatementPlan;
