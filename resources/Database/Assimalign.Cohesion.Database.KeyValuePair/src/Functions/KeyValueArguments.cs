using System;
using System.Diagnostics;

using Assimalign.Cohesion.Database.KeyValuePair.Internal;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>A stack-only view of one scalar call's primitive arguments and context.</summary>
/// <remarks>Binary values are exposed as read-only memory; the engine owns its detached argument copies.</remarks>
[DebuggerDisplay("KeyValueArguments, Count = {Count}")]
[DebuggerTypeProxy(typeof(KeyValueArguments.DebugView))]
public readonly ref struct KeyValueArguments
{
    private readonly ReadOnlySpan<object?> _values;

    /// <summary>Initializes arguments for a direct call or the engine's invocation.</summary>
    /// <param name="values">The argument values, retained only for this call.</param>
    /// <param name="context">The database and statement cancellation context.</param>
    public KeyValueArguments(ReadOnlySpan<object?> values, KeyValueFunctionContext context = default)
    {
        _values = values;
        Context = context;
    }

    /// <summary>Gets the number of arguments.</summary>
    public int Count => _values.Length;

    /// <summary>Gets the call's database and cancellation context.</summary>
    public KeyValueFunctionContext Context { get; }

    /// <summary>Gets a primitive argument; binary values are read-only memory rather than mutable arrays.</summary>
    /// <param name="index">The zero-based argument position.</param>
    /// <returns>The primitive value, read-only binary memory, or null.</returns>
    /// <exception cref="IndexOutOfRangeException">The position is outside the call.</exception>
    public object? this[int index] => _values[index] switch
    {
        byte[] bytes => (ReadOnlyMemory<byte>)bytes,
        Memory<byte> bytes => (ReadOnlyMemory<byte>)bytes,
        var value => value,
    };

    /// <summary>Gets whether an argument is NULL.</summary>
    /// <param name="index">The zero-based argument position.</param>
    /// <returns>Whether the argument is null.</returns>
    /// <exception cref="IndexOutOfRangeException">The position is outside the call.</exception>
    public bool IsNull(int index) => _values[index] is null;

    /// <summary>Reads a binary argument without exposing a writable array.</summary>
    /// <param name="index">The zero-based argument position.</param>
    /// <returns>The argument's read-only bytes.</returns>
    /// <exception cref="IndexOutOfRangeException">The position is outside the call.</exception>
    /// <exception cref="InvalidCastException">The value is NULL or not binary.</exception>
    public ReadOnlyMemory<byte> GetBinary(int index) => KeyValueFunctionValues.Binary(_values[index]);

    /// <summary>Reads a value of its exact CLR primitive type.</summary>
    /// <typeparam name="T">The expected primitive type; use GetBinary for read-only binary access.</typeparam>
    /// <param name="index">The zero-based argument position.</param>
    /// <returns>The exact typed value; a byte-array request returns a defensive copy.</returns>
    /// <exception cref="IndexOutOfRangeException">The position is outside the call.</exception>
    /// <exception cref="InvalidCastException">The value is NULL or of another type.</exception>
    /// <exception cref="NotSupportedException">The requested CLR type is not a supported primitive.</exception>
    public T Get<T>(int index) => KeyValueFunctionValues.Read<T>(_values[index]);

    internal object? RawValue(int index) => _values[index];

    private sealed class DebugView
    {
        private readonly int _count;

        public DebugView(KeyValueArguments value)
        {
            _count = value.Count;
        }

        public int Count => _count;
    }
}
