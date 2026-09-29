using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>One property-name or zero-based array-index step in a path.</summary>
/// <param name="Name">The property name, or null for an array index.</param>
/// <param name="Index">The array index, or null for a property.</param>
public readonly record struct OqlPathSegment(string? Name, int? Index);
