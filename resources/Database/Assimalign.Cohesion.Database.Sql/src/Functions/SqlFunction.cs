using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// A function a SQL engine executes: its name, kind, signature, volatility and NULL rule. The
/// engine's built-in functions and an application's own are the same kind of object, registered in
/// the same catalog (<see cref="SqlDatabaseEngineBuilder.Functions"/>) and called the same way.
/// </summary>
/// <remarks>
/// <para>
/// <b>The variant set</b> is {<see cref="SqlScalarFunction"/>, <see cref="SqlAggregateFunction"/>};
/// window and table functions are later leaves. The constructor is <c>private protected</c>: every
/// direct leaf lives in this assembly, and an application derives from one of the two leaves.
/// </para>
/// <para>
/// <b>Everything here is fixed at construction</b> and field-backed: a function's metadata never
/// changes, and reading it makes no virtual call (<c>database-area.md</c>, rule 6).
/// </para>
/// <para>
/// <b>Thread safety.</b> One instance serves every session of every database of the engine at once,
/// so a function must be stateless or thread-safe. Per-group state belongs in an
/// <see cref="SqlAggregateAccumulator"/>, which the engine creates per group and never shares.
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class SqlFunction
{
    private readonly SqlType[] _parameters;

    /// <summary>Initializes a function's metadata.</summary>
    /// <param name="name">The name calls use, matched ignoring case.</param>
    /// <param name="kind">Whether the function is a scalar or an aggregate.</param>
    /// <param name="parameters">The parameter types, in order.</param>
    /// <param name="returnType">The result type.</param>
    /// <param name="volatility">How stable the result is for the same arguments.</param>
    /// <param name="nullBehavior">What a NULL argument does.</param>
    /// <param name="variadicParameter">
    /// The type of any number of arguments after <paramref name="parameters"/>, or null for a fixed
    /// number of arguments.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/>, <paramref name="returnType"/> or a parameter type is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is empty or white space; the result type is a pseudo-type other than
    /// <see cref="SqlType.AnyElement"/>, or <see cref="SqlType.AnyElement"/> without an
    /// <see cref="SqlType.AnyElement"/> parameter to take its type from.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/>, <paramref name="volatility"/> or <paramref name="nullBehavior"/> is not defined.</exception>
    private protected SqlFunction(string name, SqlFunctionKind kind, ReadOnlySpan<SqlType> parameters, SqlType returnType,
        SqlFunctionVolatility volatility, SqlNullBehavior nullBehavior, SqlType? variadicParameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(returnType);
        if (kind is not (SqlFunctionKind.Scalar or SqlFunctionKind.Aggregate))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "The function kind is not defined.");
        }
        if (volatility is not (SqlFunctionVolatility.Immutable or SqlFunctionVolatility.Stable or SqlFunctionVolatility.Volatile))
        {
            throw new ArgumentOutOfRangeException(nameof(volatility), volatility, "The volatility is not defined.");
        }
        if (nullBehavior is not (SqlNullBehavior.ReturnsNullOnNullInput or SqlNullBehavior.CalledOnNullInput))
        {
            throw new ArgumentOutOfRangeException(nameof(nullBehavior), nullBehavior, "The NULL behavior is not defined.");
        }

        _parameters = parameters.ToArray();
        bool polymorphic = ReferenceEquals(variadicParameter, SqlType.AnyElement);
        foreach (var parameter in _parameters)
        {
            ArgumentNullException.ThrowIfNull(parameter, nameof(parameters));
            polymorphic |= ReferenceEquals(parameter, SqlType.AnyElement);
        }
        if (returnType.IsPseudo && !(ReferenceEquals(returnType, SqlType.AnyElement) && polymorphic))
        {
            throw new ArgumentException(ReferenceEquals(returnType, SqlType.AnyElement)
                ? $"Function '{name}' returns ANYELEMENT but has no ANYELEMENT parameter to take the result type from."
                : $"Function '{name}' cannot return the pseudo-type {returnType.Name}.", nameof(returnType));
        }

        Name = name;
        Kind = kind;
        Parameters = new ReadOnlyCollection<SqlType>(_parameters);
        ReturnType = returnType;
        Volatility = volatility;
        NullBehavior = nullBehavior;
        VariadicParameter = variadicParameter;
    }

    /// <summary>Gets the name calls use, matched ignoring case.</summary>
    public string Name { get; }

    /// <summary>Gets whether the function is a scalar or an aggregate.</summary>
    public SqlFunctionKind Kind { get; }

    /// <summary>Gets the parameter types, in order; empty for a function without parameters (never null).</summary>
    public IReadOnlyList<SqlType> Parameters { get; }

    /// <summary>
    /// Gets the type of any number of arguments after <see cref="Parameters"/>, or null when the
    /// function takes exactly as many arguments as it has parameters.
    /// </summary>
    public SqlType? VariadicParameter { get; }

    /// <summary>Gets the result type.</summary>
    public SqlType ReturnType { get; }

    /// <summary>Gets how stable the result is for the same arguments.</summary>
    public SqlFunctionVolatility Volatility { get; }

    /// <summary>Gets what a NULL argument does.</summary>
    public SqlNullBehavior NullBehavior { get; }

    /// <summary>
    /// Gets the call forms a diagnostic shows, for example <c>ABS(numeric)</c>; by default the
    /// name and the parameter types.
    /// </summary>
    internal string Usage
    {
        get => field ??= Describe();
        private protected init;
    }

    /// <summary>
    /// Gets how the engine types the result statically: from <see cref="ReturnType"/>, or for a
    /// standard-library function with a rule of its own, from that rule.
    /// </summary>
    internal SqlResultRule ResultRule { get; private protected init; }

    /// <summary>
    /// Gets whether the function never returns NULL, so a projection of it is not nullable:
    /// <c>COUNT</c>, which returns 0 for an empty group.
    /// </summary>
    internal bool IsNeverNull { get; private protected init; }

    /// <summary>Gets the parameter types without the read-only wrapper.</summary>
    internal ReadOnlySpan<SqlType> ParameterTypes => _parameters;

    /// <summary>Returns the function's signature, for example <c>clamp(BIGINT, BIGINT, BIGINT)</c>.</summary>
    /// <returns>The signature.</returns>
    public override string ToString() => Describe();

    /// <summary>Whether a call may pass a number of arguments.</summary>
    /// <param name="count">The number of arguments.</param>
    /// <returns><see langword="true"/> when the count matches the parameters, or reaches them for a variadic function.</returns>
    internal bool AcceptsCount(int count)
        => VariadicParameter is null ? count == _parameters.Length : count >= _parameters.Length;

    /// <summary>The declared type of the argument at a position: its parameter's, or the variadic parameter's.</summary>
    /// <param name="index">The argument's position.</param>
    /// <returns>The declared type.</returns>
    internal SqlType ParameterAt(int index) => index < _parameters.Length ? _parameters[index] : VariadicParameter!;

    /// <summary>Throws when a call passes a number of arguments the function does not take.</summary>
    /// <param name="count">The number of arguments.</param>
    /// <exception cref="ArgumentException">The count does not match.</exception>
    private protected void ThrowIfArgumentCountInvalid(int count)
    {
        if (!AcceptsCount(count))
        {
            throw new ArgumentException(
                $"Function '{Name}' takes {(VariadicParameter is null ? "exactly" : "at least")} {_parameters.Length} argument(s), but {count} were passed.",
                "arguments");
        }
    }

    private string Describe()
    {
        var text = new StringBuilder(Name).Append('(');
        for (int index = 0; index < _parameters.Length; index++)
        {
            text.Append(index == 0 ? string.Empty : ", ").Append(_parameters[index].Name);
        }
        if (VariadicParameter is not null)
        {
            text.Append(_parameters.Length == 0 ? string.Empty : ", ").Append(VariadicParameter.Name).Append(" ...");
        }

        return text.Append(')').ToString();
    }
}
