namespace System;

/// <summary>
/// Represents a value that can be one of several possible types, providing type and value information for the current
/// selection.
/// </summary>
/// <remarks>IEither is typically used to model a value that may be of one of several types, similar to a
/// discriminated union. The interface exposes the runtime type, the index of the selected type, and the value itself.
/// Implementations should ensure that the Type, TypeIndex, and Value properties are consistent with each
/// other.</remarks>
public interface IEither
{
    Type? Type { get; }
    int TypeIndex { get; }
    object? Value { get; }
}
