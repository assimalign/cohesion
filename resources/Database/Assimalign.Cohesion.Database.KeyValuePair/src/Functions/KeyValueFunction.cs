using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;

using Assimalign.Cohesion.Database.KeyValuePair.Internal;
using Assimalign.Cohesion.Database.KeyValuePair.Protocol;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>A scalar function registered once for every database of a key-value engine.</summary>
/// <remarks>
/// An instance serves concurrent sessions and must be stateless or thread-safe. The public Invoke
/// member owns signature checks, NULL handling, cancellation and result validation; applications
/// implement only InvokeCore. The context exposes no storage/session mutation capability.
/// Callbacks are trusted application code; external side effects and captured state are not sandboxed.
/// A non-strict call enters the body exactly once; strict NULL input skips it.
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract partial class KeyValueFunction
{
    private readonly DatabaseType[] _parameters;

    /// <summary>Initializes immutable scalar metadata.</summary>
    /// <param name="name">The shared function identifier; default is refused.</param>
    /// <param name="parameters">Exact primitive argument types, in order.</param>
    /// <param name="returnType">The declared primitive result type; NULL results remain permitted.</param>
    /// <param name="nullBehavior">Whether NULL arguments skip or enter the body.</param>
    /// <exception cref="ArgumentException">The name is default.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A type is not a supported primitive, or the NULL policy is undefined.</exception>
    protected KeyValueFunction(KeyValueFunctionName name, ReadOnlySpan<DatabaseType> parameters,
        DatabaseType returnType, KeyValueNullBehavior nullBehavior = KeyValueNullBehavior.ReturnsNullOnNullInput)
    {
        if (name.Value is null) { throw new ArgumentException(ErrorMessages.FunctionNameRequired, nameof(name)); }
        if ((byte)returnType > (byte)DatabaseType.Guid)
        {
            throw new ArgumentOutOfRangeException(nameof(returnType), returnType, ErrorMessages.FunctionReturnTypeUnsupported);
        }
        if (nullBehavior is not (KeyValueNullBehavior.ReturnsNullOnNullInput or KeyValueNullBehavior.CalledOnNullInput))
        {
            throw new ArgumentOutOfRangeException(nameof(nullBehavior), nullBehavior, ErrorMessages.FunctionNullBehaviorInvalid);
        }
        _parameters = parameters.ToArray();
        foreach (DatabaseType parameter in _parameters)
        {
            if ((byte)parameter > (byte)DatabaseType.Guid)
            {
                throw new ArgumentOutOfRangeException(nameof(parameters), parameter, ErrorMessages.FunctionParameterTypeUnsupported);
            }
        }
        Name = name;
        Parameters = new ReadOnlyCollection<DatabaseType>(_parameters);
        ReturnType = returnType;
        NullBehavior = nullBehavior;
    }

    /// <summary>Gets the immutable, case-insensitive function identifier.</summary>
    public KeyValueFunctionName Name { get; }

    /// <summary>Gets the immutable exact primitive signature, empty for a parameterless function.</summary>
    public IReadOnlyList<DatabaseType> Parameters { get; }

    /// <summary>Gets the declared primitive result type.</summary>
    public DatabaseType ReturnType { get; }

    /// <summary>Gets whether the body receives NULL arguments.</summary>
    public KeyValueNullBehavior NullBehavior { get; }

    /// <summary>Validates and invokes one scalar call exactly once.</summary>
    /// <param name="arguments">The primitive arguments and database/cancellation context.</param>
    /// <returns>The declared primitive value, detached binary bytes, or null.</returns>
    /// <exception cref="ArgumentException">The direct call has a different count or non-NULL argument type.</exception>
    /// <exception cref="OperationCanceledException">The statement's own token is canceled.</exception>
    /// <exception cref="DatabaseException">The callback failed (CV05003), returned another type (CV02016), or supplied its own database failure.</exception>
    /// <exception cref="IOException">The callback's original device failure; engine execution applies its existing classification.</exception>
    /// <exception cref="StorageException">The callback's original storage failure.</exception>
    /// <exception cref="TimeoutException">The callback's original timeout.</exception>
    public object? Invoke(scoped in KeyValueArguments arguments)
    {
        if (arguments.Count != _parameters.Length)
        {
            throw new ArgumentException(string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.FunctionArgumentCount, Name, _parameters.Length, arguments.Count), nameof(arguments));
        }
        bool hasNull = false;
        for (int index = 0; index < _parameters.Length; index++)
        {
            object? value = arguments.RawValue(index);
            if (value is null) { hasNull = true; continue; }
            if (!KeyValueFunctionValues.TryGetType(value, out DatabaseType type) || type != _parameters[index])
            {
                throw new ArgumentException(string.Format(CultureInfo.CurrentCulture,
                    ErrorMessages.FunctionArgumentType, Name, index, _parameters[index]), nameof(arguments));
            }
        }
        arguments.Context.CancellationToken.ThrowIfCancellationRequested();
        if (hasNull && NullBehavior == KeyValueNullBehavior.ReturnsNullOnNullInput) { return null; }
        object? result;
        try
        {
            result = InvokeCore(in arguments);
        }
        catch (Exception error) when (error is not (DatabaseException or StorageException or IOException
            or TimeoutException or InsufficientExecutionStackException or OutOfMemoryException)
            && !(error is OperationCanceledException && arguments.Context.CancellationToken.IsCancellationRequested))
        {
            string message = string.Format(CultureInfo.CurrentCulture, ErrorMessages.FunctionCallbackFailed, Name, error.Message);
            throw new DatabaseException(message,
                [new Diagnostic { Code = "CV05003", Message = message, Severity = DiagnosticSeverity.Error }], error);
        }
        if (!KeyValueFunctionValues.TryGetType(result, out DatabaseType resultType)
            || result is not null && resultType != ReturnType)
        {
            throw DatabaseException.CreateKeyValuePairFailure("CV02016", string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.FunctionResultType, Name, result?.GetType().Name ?? "NULL", ReturnType));
        }
        return KeyValueFunctionValues.Snapshot(result, nameof(result));
    }

    /// <summary>Computes the scalar result; called only through the public invocation contract.</summary>
    /// <param name="arguments">Validated arguments; binary values are read-only through their accessors.</param>
    /// <returns>The declared primitive type or null; binary results are detached before delivery.</returns>
    protected abstract object? InvokeCore(scoped in KeyValueArguments arguments);

    internal ReadOnlySpan<DatabaseType> ParameterTypes => _parameters;
}
