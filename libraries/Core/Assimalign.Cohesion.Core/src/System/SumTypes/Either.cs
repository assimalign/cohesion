namespace System;

public abstract record class Either
{
    internal Either() { }

    protected virtual Type? Type { get; }
    protected virtual int TypeIndex { get; }
    protected virtual object? Value { get; }
}
