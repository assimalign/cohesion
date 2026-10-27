using System;

using Assimalign.Cohesion.Database.Graph.Internal;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>A borrowed, typed view of one callback's query arguments.</summary>
/// <remarks>Callbacks must not retain or mutate borrowed input values.</remarks>
[System.Diagnostics.DebuggerDisplay("GraphArguments {Count}")]
[System.Diagnostics.DebuggerTypeProxy(typeof(GraphArguments.DebugView))]
public readonly ref struct GraphArguments
{
    private readonly ReadOnlySpan<object?> _values;
    /// <summary>Initializes arguments over borrowed values.</summary>
    /// <param name="values">The values in source order.</param><param name="context">The statement context.</param>
    public GraphArguments(ReadOnlySpan<object?> values, GraphFunctionContext context = default) { _values = values; Context = context; }
    /// <summary>Gets the argument count.</summary>
    public int Count => _values.Length;
    /// <summary>Gets a value by zero-based ordinal.</summary><param name="index">The ordinal.</param><returns>The value, possibly NULL.</returns>
    public object? this[int index] => _values[index];
    /// <summary>Gets the borrowed statement context.</summary>
    public GraphFunctionContext Context { get; }
    /// <summary>Gets a typed value. NULL maps to default only for nullable/reference types.</summary>
    /// <typeparam name="T">The declared CLR query type.</typeparam><param name="index">The ordinal.</param><returns>The exact typed value.</returns>
    /// <exception cref="InvalidCastException">The value has another CLR type, or NULL is requested as a nonnullable value type.</exception>
    /// <exception cref="IndexOutOfRangeException">The ordinal is outside the argument list.</exception>
    public T Get<T>(int index)
    {
        object? value = _values[index];
        if (value is null && default(T) is not null) { throw new InvalidCastException(ErrorMessages.FunctionTypeUnsupported); }
        return (T)value!;
    }
    // Adapter-owned conversion precedes the application delegate. A cast failure here is
    // an input value failure; an InvalidCastException from the delegate remains CG05021.
    internal T GetParameter<T>(int index)
    {
        try { return Get<T>(index); }
        catch (InvalidCastException error)
        { throw new GraphPlanningException("CG02013", ErrorMessages.FunctionArgumentConversion, innerException: error); }
    }
    /// <summary>Gets whether an argument is NULL.</summary><param name="index">The ordinal.</param><returns>Whether NULL.</returns>
    public bool IsNull(int index) => _values[index] is null;
    internal ReadOnlySpan<object?> Values => _values;
    private sealed class DebugView
    {
        public DebugView(GraphArguments value) { Count = value.Count; }
        public int Count { get; }
    }
}
