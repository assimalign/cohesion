namespace System;

public interface IEither
{
    Type? Type { get; }
    int TypeIndex { get; }
    object? Value { get; }
}
