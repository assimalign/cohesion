using System;
using System.Collections;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// The functions an engine under construction will execute: its builder's
/// <see cref="SqlDatabaseEngineBuilder.Functions"/>, with the standard library already registered
/// through the same <see cref="Add"/> an application calls.
/// </summary>
/// <remarks>
/// <para>
/// <b>One flat namespace, engine-wide.</b> A registered function is visible in every database of
/// the engine (owner decision 61 of 2026-10-09); an application that wants separation builds
/// another engine. Names match ignoring case. A name may carry several overloads of one kind,
/// distinguished by their parameter types; the planner chooses among them for each call.
/// </para>
/// <para>
/// <b>What <see cref="Add"/> refuses</b> (owner decision 62): a name that is not an identifier, a
/// special form (<c>COALESCE</c>, <c>NULLIF</c>, <c>CASE</c>, <c>CAST</c>, <c>EXTRACT</c>, which
/// stay grammar), a SQL keyword, a type name, or a name the dialect reserves for a built-in that does
/// not execute yet (<c>TRIM</c>, <c>NOW</c>, ...); a name and parameter-type list already registered;
/// an overload of a standard-library name that takes a number of arguments the built-in takes, so a
/// standard-library function can never be replaced or made ambiguous (an overload of another arity,
/// <c>upper(TEXT, BIGINT)</c>, is allowed); a scalar and an aggregate under one name; and any call
/// once the engine's build began. There is no removal.
/// </para>
/// <para>
/// The engine's build freezes the collection into the engine's <see cref="SqlFunctionCatalog"/>
/// (<see cref="SqlDatabaseEngine.Functions"/>). Like the builder, the collection is not
/// thread-safe.
/// </para>
/// </remarks>
public sealed class SqlFunctionCollection : IReadOnlyCollection<SqlFunction>
{
    private readonly List<SqlFunction> _functions = [];
    private readonly Action? _ensureMutable;

    /// <summary>Initializes a collection holding the standard library.</summary>
    /// <param name="ensureMutable">Throws once the owner no longer accepts registrations; null for none.</param>
    internal SqlFunctionCollection(Action? ensureMutable)
    {
        _ensureMutable = ensureMutable;
        SqlStandardLibrary.Register(this);
    }

    /// <summary>Gets the number of registered functions, every overload counted.</summary>
    public int Count => _functions.Count;

    /// <summary>Registers a function.</summary>
    /// <param name="function">The function; one instance serves every session, so it must be thread-safe.</param>
    /// <returns>This collection.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="function"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The function's name is not an identifier, or is a special form, a SQL keyword, a type name or
    /// a name the dialect reserves.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The engine's build began; a function of the same name and parameter types is registered; the
    /// name is registered for the other kind (scalar or aggregate); or the function overloads a
    /// standard-library function for a number of arguments the built-in takes.
    /// </exception>
    public SqlFunctionCollection Add(SqlFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        _ensureMutable?.Invoke();
        SqlStandardLibrary.ValidateName(function.Name);

        foreach (var registered in _functions)
        {
            if (!string.Equals(registered.Name, function.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (registered.Kind != function.Kind)
            {
                throw new InvalidOperationException(
                    $"Function '{function.Name}' is registered as {Article(registered.Kind)}; " +
                    $"{Article(function.Kind)} of the same name cannot be added.");
            }

            if (HasSameSignature(registered, function))
            {
                throw new InvalidOperationException(
                    $"Function {function} is already registered{(SqlStandardLibrary.Contains(registered) ? " by the standard library" : string.Empty)}; " +
                    "a function cannot be replaced, only overloaded with other parameter types.");
            }
        }

        if (!SqlStandardLibrary.Contains(function) && SqlStandardLibrary.FindOverlap(function) is { } builtin)
        {
            throw new InvalidOperationException(
                $"Function {function} takes calls the standard library's {builtin.Usage} takes, so it would replace the built-in for them " +
                "or make them ambiguous, in stored CHECK constraints too; a standard-library function cannot be replaced. " +
                $"Register the function under another name, or with a number of parameters {builtin.Name} does not take.");
        }

        _functions.Add(function);
        return this;
    }

    /// <summary>Whether a function of a name is registered, ignoring case.</summary>
    /// <param name="name">The name.</param>
    /// <returns><see langword="true"/> when at least one overload is registered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public bool Contains(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var function in _functions)
        {
            if (string.Equals(function.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns the registered functions in registration order, the standard library first.</summary>
    /// <returns>The enumerator.</returns>
    public IEnumerator<SqlFunction> GetEnumerator() => _functions.GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Freezes the registrations into the catalog the engine executes; phase 2 of the build.</summary>
    /// <returns>The catalog.</returns>
    internal SqlFunctionCatalog Freeze() => new(_functions);

    private static bool HasSameSignature(SqlFunction left, SqlFunction right)
    {
        var leftParameters = left.ParameterTypes;
        var rightParameters = right.ParameterTypes;
        if (leftParameters.Length != rightParameters.Length || (left.VariadicParameter is null) != (right.VariadicParameter is null))
        {
            return false;
        }

        for (int index = 0; index < leftParameters.Length; index++)
        {
            if (!leftParameters[index].IsSameParameterType(rightParameters[index]))
            {
                return false;
            }
        }

        return left.VariadicParameter is null || left.VariadicParameter.IsSameParameterType(right.VariadicParameter!);
    }

    private static string Article(SqlFunctionKind kind) => kind == SqlFunctionKind.Scalar ? "a scalar function" : "an aggregate";
}
