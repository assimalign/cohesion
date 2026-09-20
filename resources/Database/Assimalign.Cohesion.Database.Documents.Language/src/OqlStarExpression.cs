using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>The entire source document, or the row-count operand of COUNT(*).</summary>
/// <param name="location">The source span.</param>
public sealed class OqlStarExpression(Location? location = null) : OqlExpression(location);
