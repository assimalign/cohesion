using System;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Catalog;

/// <summary>A named single-property type or existence constraint on a label or relationship type.</summary>
/// <param name="DefinitionId">The owning label or relationship-type identity.</param>
/// <param name="Name">The ordinal name, unique across all database indexes and constraints.</param>
/// <param name="PropertyKey">The constrained property key.</param>
/// <param name="Type">The required scalar type; null denotes an existence constraint.</param>
/// <remarks>A type constraint permits absent and null values. An existence constraint requires a non-null value.</remarks>
public readonly record struct GraphConstraintMetadata(Guid DefinitionId, string Name, string PropertyKey, DatabaseType? Type = null);
