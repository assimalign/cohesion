using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Text;

namespace Assimalign.Cohesion.Resilience;

public readonly struct Outcome : IEither
{
    private readonly int _typeIndex;
    private readonly object? _typeResult;

    public Outcome(bool isSuccess)
    {
        _typeResult = isSuccess;
        _typeIndex = 1;
    }

    public Outcome(Exception exception)
    {
        _typeResult = ExceptionDispatchInfo.Capture(exception);
        _typeIndex = 2;
    }

    int IEither.TypeIndex => _typeIndex;
    Type IEither.Type => _typeIndex switch
    {
        1 => typeof(bool),
        2 => typeof(Exception),
        _ => throw new InvalidOperationException()
    };
    object? IEither.Value => _typeResult;
    bool AsT1 => (bool)_typeResult!;
    Exception AsT2 => ((ExceptionDispatchInfo)_typeResult!).SourceException;


    public static implicit operator Outcome(bool value) => new Outcome(value);
    public static implicit operator Outcome(Exception value) => new Outcome(value);
    public static explicit operator bool(Outcome either) => either.AsT1;
    public static explicit operator Exception(Outcome either) => either.AsT2;
}
