using System;

namespace Assimalign.Cohesion.Database;

/// <summary>Describes a custom type declaration.</summary>
public interface IDatabaseSchemaType
{
    /// <summary>Gets the represented CLR type.</summary>
    Type ClrType { get; }

    /// <summary>Gets the decimal precision, when configured.</summary>
    int? Precision { get; }

    /// <summary>Gets the decimal scale, when configured.</summary>
    int? Scale { get; }
}
