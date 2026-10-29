using System;

namespace Assimalign.Cohesion.Database.Graph.Internal;

[Flags]
internal enum GraphFunctionKind
{
    Unknown = 0, Node = 1, Relationship = 2, Path = 4, String = 8, Integral = 16,
    Boolean = 32, List = 64, Map = 128, Date = 256, Timestamp = 512,
    NonIntegral = 1024, Number = Integral | NonIntegral,
    Null = 2048, // Reduction closure only; stripped before reporting a known non-NULL kind.
    Scalar = String | Number | Boolean | Date | Timestamp,
}
