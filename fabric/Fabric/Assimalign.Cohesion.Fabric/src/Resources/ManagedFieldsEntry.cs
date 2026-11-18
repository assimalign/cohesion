using System.Collections.Generic;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// The spec fields one manager owns, for apply semantics: an apply from a manager changes only
/// the fields it owns, and a conflicting apply from another manager is refused.
/// </summary>
/// <param name="Manager">The manager, such as <c>apphost</c> or <c>cli:alice</c>.</param>
/// <param name="Fields">The dotted spec paths it owns, such as <c>spec.replicas</c>.</param>
public sealed record ManagedFieldsEntry(string Manager, IReadOnlyList<string> Fields);
