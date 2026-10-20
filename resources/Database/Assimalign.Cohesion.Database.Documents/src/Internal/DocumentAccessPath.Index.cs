using System.Collections.Generic;
using Assimalign.Cohesion.Database.Documents.Catalog;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed record DocumentIndexPath(DocumentIndexMetadata Index, DocumentSeekBound? Lower, DocumentSeekBound? Upper, IReadOnlyList<object>? EqualityPrefix = null) : DocumentAccessPath;
