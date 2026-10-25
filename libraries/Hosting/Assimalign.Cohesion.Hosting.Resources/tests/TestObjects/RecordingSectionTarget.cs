using System;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

/// <summary>Stands in for a feature's atomic store: it records every apply and keeps the last good value.</summary>
internal sealed class RecordingSectionTarget<T>
{
    private readonly Func<T, string?>? _validate;

    public RecordingSectionTarget(T initial, Func<T, string?>? validate = null)
    {
        Current = initial;
        _validate = validate;
    }

    public T Current { get; private set; }

    public int Applies { get; private set; }

    public string? Apply(T value)
    {
        Applies++;
        string? error = _validate?.Invoke(value);
        if (error is null)
        {
            Current = value;
        }

        return error;
    }
}
