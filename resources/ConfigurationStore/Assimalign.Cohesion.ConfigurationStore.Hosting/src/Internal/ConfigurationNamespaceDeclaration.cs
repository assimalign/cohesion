using System.Collections.Generic;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting.Internal;

/// <summary>
/// A namespace declared through <c>AddNamespace</c>, registered as a singleton so the repository
/// seeds from the resolved declarations.
/// </summary>
/// <param name="Name">The namespace name.</param>
/// <param name="Values">The declared seed values.</param>
internal sealed record ConfigurationNamespaceDeclaration(
    string Name,
    IReadOnlyDictionary<string, string?> Values);
