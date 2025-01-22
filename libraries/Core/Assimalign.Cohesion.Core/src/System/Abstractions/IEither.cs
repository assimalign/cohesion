namespace System;

public interface IEither
{
    int TypeIndex { get; }
    Type Type { get; }
    object Value { get; }
}
