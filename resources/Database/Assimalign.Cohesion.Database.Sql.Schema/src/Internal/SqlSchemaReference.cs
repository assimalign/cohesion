using System;

namespace Assimalign.Cohesion.Database.Sql.Schema.Internal;

/// <summary>A table reference.</summary>
/// <param name="Member">The foreign-key member name.</param>
/// <param name="TargetType">The referenced table row type.</param>
internal sealed record SqlSchemaReference(string Member, Type TargetType);
