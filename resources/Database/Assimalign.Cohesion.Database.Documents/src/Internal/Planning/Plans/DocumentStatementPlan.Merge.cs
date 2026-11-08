using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed record DocumentMergePlan(DocumentCollectionMetadata Collection, OqlMergeExpression Statement,
    DocumentStatementPlan Source, string SourceName, string TargetName, OqlExpression Key,
    DocumentMergeScope LookupScope, DocumentMergeScope MatchedScope, DocumentMergeScope InsertScope,
    DocumentUpdatePlan? Update, DocumentLogicalPlan? Returning) : DocumentStatementPlan;
