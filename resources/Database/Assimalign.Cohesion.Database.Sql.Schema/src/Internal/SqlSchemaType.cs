using System;

namespace Assimalign.Cohesion.Database.Sql.Schema.Internal;

/// <summary>A custom type declaration.</summary>
/// <param name="ClrType">The represented CLR type.</param>
/// <param name="Precision">The decimal precision, when configured.</param>
/// <param name="Scale">The decimal scale, when configured.</param>
internal sealed record SqlSchemaType(
    Type ClrType,
    int? Precision,
    int? Scale);
