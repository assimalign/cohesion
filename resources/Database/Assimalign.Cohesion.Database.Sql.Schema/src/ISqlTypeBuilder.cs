using System;

namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>Configures a custom database type.</summary>
public interface ISqlTypeBuilder
{
    /// <summary>Represents the type as a fixed-precision decimal.</summary>
    /// <param name="precision">The total number of digits.</param>
    /// <param name="scale">The number of fractional digits.</param>
    /// <exception cref="ArgumentOutOfRangeException">The precision is less than one, the scale is negative, or the scale exceeds the precision.</exception>
    void Decimal(int precision, int scale);
}
