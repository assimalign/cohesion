using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

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

    // The storage type a result has without a conversion: the declared one, or NULL's for a
    // pseudo-type result, which only a NULL result matches on the fast path.
    private readonly DatabaseType _resultType;

    // For an ANYELEMENT result, the position of the first ANYELEMENT argument, whose type the
    // result has; -1 otherwise.
    private readonly int _elementIndex = -1;

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
        FixedCoercionTargets = variadicParameter is null ? SqlFunctionResolver.ComputeCoercionTargets(this, _parameters.Length) : null;
        _resultType = returnType.IsPseudo ? DatabaseType.Null : returnType.Storage.Type;
        if (ReferenceEquals(returnType, SqlType.AnyElement))
        {
            int element = Array.IndexOf(_parameters, SqlType.AnyElement);
            _elementIndex = element >= 0 ? element : _parameters.Length; // else the variadic tail is the ANYELEMENT
        }
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
    /// Gets whether the function never returns NULL, as <c>COUNT</c>, which returns 0 for an empty
    /// group: a projection of a bare call to it is reported as not nullable. A leaf sets it in its
    /// constructor; a function that sets it and returns NULL fails the statement as <c>COHSQLE007</c>.
    /// </summary>
    public bool IsNeverNull { get; protected init; }

    /// <summary>
    /// Gets the call forms a diagnostic shows, for example <c>ABS(numeric)</c>; by default the
    /// name and the parameter types.
    /// </summary>
    /// <remarks>
    /// Set only by the standard library, whose permissive built-ins keep the diagnostics they had
    /// before typed signatures (owner decision 67). It goes with <see cref="ResultRule"/> when
    /// <c>UPPER</c>, <c>LOWER</c>, <c>LENGTH</c> and <c>ABS</c> are tightened to typed overloads.
    /// </remarks>
    internal string Usage
    {
        get => field ??= Describe();
        private protected init;
    }

    /// <summary>
    /// Gets how the engine types the result statically: from <see cref="ReturnType"/>, or for a
    /// standard-library function with a rule of its own, from that rule.
    /// </summary>
    /// <remarks>
    /// Only <c>ABS</c> has a rule of its own (an exact integer widens to BIGINT), the one result a
    /// declared type cannot express while the built-in stays permissive (owner decision 67).
    /// </remarks>
    internal SqlResultRule ResultRule { get; private protected init; }

    /// <summary>Gets the parameter types without the read-only wrapper.</summary>
    internal ReadOnlySpan<SqlType> ParameterTypes => _parameters;

    /// <summary>
    /// Gets the storage type each argument of a function with a fixed number of parameters converts
    /// to before the call, <see cref="DatabaseType.Null"/> for a pseudo-type; null when every parameter
    /// is a pseudo-type or the function is variadic. Computed once and shared by every bound call.
    /// </summary>
    internal DatabaseType[]? FixedCoercionTargets { get; }

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

    /// <summary>
    /// Checks what a core returned against the declaration, which plans, result-set metadata and
    /// outer calls rely on: a value of the declared type, or of a type that widens to it implicitly
    /// (converted here), a JSON value read as text or bytes, NULL unless the function never returns
    /// NULL, and for an <see cref="SqlType.AnyElement"/> result the type of the call's
    /// <see cref="SqlType.AnyElement"/> arguments. One comparison when the type is the declared one.
    /// </summary>
    /// <param name="result">What the core returned.</param>
    /// <param name="arguments">The call's arguments; none for an aggregate's result, whose ANYELEMENT type is not checked.</param>
    /// <returns>The result, converted to the declared type when it widened to it.</returns>
    /// <exception cref="DatabaseException">The result does not match (<c>COHSQLE007</c>, naming the function).</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SqlValue CheckResult(in SqlValue result, scoped in SqlArguments arguments)
        => HasDeclaredResultType(result.Type, in arguments) ? result : CheckResultSlow(result, in arguments);

    /// <summary>
    /// The fast test of <see cref="CheckResult"/>, on the result's type alone, so a caller that
    /// holds the result checks it where it lies, without copying it: the declared type (NULL too,
    /// unless the function never returns NULL), or for an <see cref="SqlType.AnyElement"/> result,
    /// the type of the call's first <see cref="SqlType.AnyElement"/> argument (<c>UPPER</c> over
    /// text). Anything else goes to <see cref="CheckResultSlow"/>, which converts or refuses it.
    /// </summary>
    /// <param name="type">The result's storage type.</param>
    /// <param name="arguments">The call's arguments.</param>
    /// <returns><see langword="true"/> when the result is returned as it is.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool HasDeclaredResultType(DatabaseType type, scoped in SqlArguments arguments)
        => type == _resultType
            ? _resultType != DatabaseType.Null || !IsNeverNull
            : (uint)_elementIndex < (uint)arguments.Count && arguments.TypeAt(_elementIndex) == type;

    /// <summary>
    /// The rest of <see cref="CheckResult"/>, for a result the fast test did not pass: NULL from a
    /// function that never returns it, a widening, a JSON value, an <see cref="SqlType.AnyElement"/>
    /// result of another argument, or a mismatch.
    /// </summary>
    /// <param name="result">What the core returned.</param>
    /// <param name="arguments">The call's arguments.</param>
    /// <returns>The result, converted to the declared type when it widened to it.</returns>
    /// <exception cref="DatabaseException">The result does not match (<c>COHSQLE007</c>, naming the function).</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal SqlValue CheckResultSlow(in SqlValue result, scoped in SqlArguments arguments)
    {
        if (result.IsNull)
        {
            return IsNeverNull ? throw ResultMismatch("NULL", "it declares that it never returns NULL") : result;
        }

        string returned = SqlType.NameOf(result.Type);
        if (ReturnType.IsPseudo)
        {
            if (ResultRule != SqlResultRule.Declared)
            {
                return result; // a standard-library result rule of its own (ABS widens integers)
            }

            for (int index = 0; index < arguments.Count; index++)
            {
                if (ReferenceEquals(ParameterAt(index), SqlType.AnyElement) && !arguments[index].IsNull)
                {
                    return arguments[index].Type == result.Type
                        ? result
                        : throw ResultMismatch(returned, $"its ANYELEMENT arguments are {SqlType.NameOf(arguments[index].Type)}");
                }
            }

            return result; // no ANYELEMENT argument to compare with: NULLs only, or an aggregate's result
        }
        if (_resultType == DatabaseType.Json && result.Type == DatabaseType.String
            || _resultType == DatabaseType.JsonBinary && result.Type == DatabaseType.Binary)
        {
            return result;
        }

        return SqlFunctionResolver.TryWiden(result, _resultType, this, out var widened)
            ? widened
            : throw ResultMismatch(returned, $"it declares {ReturnType.Name}");
    }

    private SqlEvaluationException ResultMismatch(string returned, string declared)
        => SqlEvaluationException.FunctionFailed(Name, new InvalidCastException($"The function returned {returned}, but {declared}."));

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
