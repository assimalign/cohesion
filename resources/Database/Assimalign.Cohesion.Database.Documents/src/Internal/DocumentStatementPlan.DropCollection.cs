using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed record DocumentDropCollectionPlan(OqlDropCollectionExpression Statement) : DocumentStatementPlan;
