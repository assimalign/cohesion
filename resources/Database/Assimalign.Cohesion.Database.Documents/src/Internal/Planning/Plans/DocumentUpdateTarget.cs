using System.Collections.Generic;
using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed record DocumentUpdateTarget(OqlPathExpression Path, IReadOnlyList<OqlPathSegment> Segments, OqlExpression? Value);
