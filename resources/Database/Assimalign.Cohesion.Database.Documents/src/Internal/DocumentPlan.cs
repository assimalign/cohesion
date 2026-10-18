using System.Collections.Generic;
using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Internal;

// The logical stage validates document semantics; the physical stage binds a
// collection and chooses candidate retrieval without changing those semantics.
internal abstract record DocumentStatementPlan;
internal sealed record DocumentLogicalPlan(OqlSelectExpression Query, IReadOnlyList<DocumentProjection> Projections, bool IsGrouped);
internal sealed record DocumentPlan(DocumentLogicalPlan Logical, DocumentCollectionMetadata Collection, DocumentAccessPath Access) : DocumentStatementPlan;
internal sealed record DocumentSystemCollectionPlan(DocumentLogicalPlan Logical, string Name) : DocumentStatementPlan;
internal sealed record DocumentDeletePlan(DocumentCollectionMetadata Collection, OqlDeleteExpression Statement, DocumentAccessPath Access) : DocumentStatementPlan;
internal sealed record DocumentUpdatePlan(DocumentCollectionMetadata Collection, OqlUpdateExpression Statement,
    IReadOnlyList<DocumentUpdateTarget> Targets, DocumentAccessPath Access) : DocumentStatementPlan;
internal sealed record DocumentUpdateTarget(OqlPathExpression Path, IReadOnlyList<OqlPathSegment> Segments, OqlExpression? Value);
internal sealed record DocumentInsertPlan(DocumentCollectionMetadata Collection, OqlInsertExpression Statement) : DocumentStatementPlan;
internal sealed record DocumentInsertSelectPlan(DocumentCollectionMetadata Collection, OqlInsertExpression Statement,
    DocumentStatementPlan Source, int KeyOrdinal, int? ValueOrdinal) : DocumentStatementPlan;
internal sealed record DocumentUpsertPlan(DocumentCollectionMetadata Collection, OqlUpsertExpression Statement,
    DocumentStatementPlan? Source = null, int KeyOrdinal = 0, int? ValueOrdinal = null) : DocumentStatementPlan;
internal sealed record DocumentCreateCollectionPlan(OqlCreateCollectionExpression Statement) : DocumentStatementPlan;
internal sealed record DocumentDropCollectionPlan(OqlDropCollectionExpression Statement) : DocumentStatementPlan;
internal sealed record DocumentCreateIndexPlan(DocumentCollectionMetadata Collection, string IndexName, string Path) : DocumentStatementPlan;
internal sealed record DocumentDropIndexPlan(DocumentCollectionMetadata Collection, string IndexName) : DocumentStatementPlan;
internal sealed record DocumentProjection(string Name, OqlExpression Expression, DatabaseType Type);
internal abstract record DocumentAccessPath;
internal sealed record DocumentScanPath : DocumentAccessPath;
internal sealed record DocumentIndexPath(DocumentIndexMetadata Index, DocumentSeekBound? Lower, DocumentSeekBound? Upper) : DocumentAccessPath;
internal readonly record struct DocumentSeekBound(object Value, bool Inclusive);
