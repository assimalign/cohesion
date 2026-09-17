using System.Collections.Generic;
using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

// The logical stage validates document semantics; the physical stage binds a
// collection and chooses candidate retrieval without changing those semantics.
internal sealed record DocumentLogicalPlan(OqlSelectExpression Query, IReadOnlyList<DocumentProjection> Projections, bool IsGrouped);
internal sealed record DocumentPlan(DocumentLogicalPlan Logical, DocumentCollectionMetadata Collection, DocumentAccessPath Access);
internal sealed record DocumentProjection(string Name, OqlExpression Expression);
internal abstract record DocumentAccessPath;
internal sealed record DocumentScanPath : DocumentAccessPath;
internal sealed record DocumentIndexPath(DocumentIndexMetadata Index, DocumentSeekBound? Lower, DocumentSeekBound? Upper) : DocumentAccessPath;
internal readonly record struct DocumentSeekBound(object Value, bool Inclusive);
