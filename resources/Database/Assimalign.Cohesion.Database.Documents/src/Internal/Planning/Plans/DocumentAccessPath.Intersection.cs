using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Documents.Internal;

/// <summary>Intersect ordinal document identities before reading JSON; retain the complete residual.</summary>
internal sealed record DocumentIntersectionPath(IReadOnlyList<DocumentIndexPath> Inputs) : DocumentAccessPath;
