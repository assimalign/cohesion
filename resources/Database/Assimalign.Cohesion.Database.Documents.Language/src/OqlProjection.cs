using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A projected expression and optional output name.</summary>
/// <param name="Expression">The projected expression.</param>
/// <param name="Alias">The optional output name.</param>
public sealed record OqlProjection(OqlExpression Expression, string? Alias);
