using System;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Catalog;

/// <summary>A property key declared by a label or relationship type.</summary>
/// <param name="DefinitionId">The owning label or relationship type identity.</param>
/// <param name="Name">The ordinal property key.</param>
/// <param name="Type">The required value type, or null for an unconstrained property.</param>
/// <param name="Required">Whether each object carrying the definition requires this property.</param>
public readonly record struct GraphPropertyKeyMetadata(Guid DefinitionId, string Name,
    DatabaseType? Type = null, bool Required = false);
