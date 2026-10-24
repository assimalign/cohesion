using System;
using System.Collections.Generic;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>A native or developer scalar function with one checked invocation pipeline.</summary>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class GraphScalarFunction : GraphFunction
{
    /// <summary>Initializes immutable scalar metadata.</summary>
    /// <param name="name">The name.</param><param name="parameters">The parameter shapes.</param>
    /// <param name="returnType">The result shape.</param><param name="nullBehavior">The NULL contract.</param>
    /// <exception cref="ArgumentException">The name is default/invalid or a parameter shape is null.</exception>
    /// <exception cref="ArgumentNullException">Parameter metadata or return metadata is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The NULL behavior is unknown.</exception>
    protected GraphScalarFunction(GraphFunctionName name, IEnumerable<GraphFunctionType> parameters, GraphFunctionType returnType,
        GraphNullBehavior nullBehavior = GraphNullBehavior.Strict) : base(name, parameters, returnType, nullBehavior, scalar: true) { }
    /// <summary>Invokes this function after checking arity, cancellation, NULLs and declared value shapes.</summary>
    /// <param name="arguments">Borrowed arguments and context.</param><returns>The result.</returns>
    /// <exception cref="DatabaseException">The arguments or callback violate the function contract.</exception>
    /// <exception cref="OperationCanceledException">Statement cancellation was requested.</exception>
    public object? Invoke(scoped in GraphArguments arguments)
    {
        arguments.Context.CancellationToken.ThrowIfCancellationRequested();
        if (!AcceptsArity(arguments.Count)) { throw GraphPlanningException.FunctionContract(Name); }
        if (NullBehavior == GraphNullBehavior.Strict)
        { for (int i = 0; i < arguments.Count; i++) { if (arguments[i] is null) { return null; } } }
        if (!IsNative)
        { for (int i = 0; i < arguments.Count; i++) { GraphPlanningException.ValidateFunctionValue(this, arguments[i], Parameters[i]); } }
        try
        {
            object? result = InvokeCore(in arguments);
            arguments.Context.CancellationToken.ThrowIfCancellationRequested();
            if (!IsNative) { GraphPlanningException.ValidateFunctionValue(this, result, DeclaredReturnType); }
            return result;
        }
        catch (Exception error) when (GraphPlanningException.IsExternalFunctionFailure(error, arguments.Context.CancellationToken))
        { throw GraphPlanningException.FunctionFailed(Name, error); }
    }
    /// <summary>Evaluates already checked arguments.</summary><param name="arguments">Borrowed arguments.</param><returns>The value.</returns>
    protected abstract object? InvokeCore(scoped in GraphArguments arguments);
    /// <summary>Creates a niladic typed scalar.</summary><typeparam name="TResult">The result CLR type.</typeparam>
    /// <param name="name">The name.</param><param name="body">The callback.</param><returns>The immutable function.</returns>
    /// <exception cref="ArgumentException">The name is default/invalid, or a CLR type is unsupported.</exception>
    /// <exception cref="ArgumentNullException">The callback is null.</exception>
    public static GraphScalarFunction Create<TResult>(GraphFunctionName name, Func<TResult> body)
        => new GraphTypedScalarFunction<TResult>(name, body);
    /// <summary>Creates a typed scalar with 1 argument(s).</summary>
    /// <typeparam name="T1">A query argument CLR type.</typeparam>
    /// <typeparam name="TResult">The result CLR type.</typeparam>
    /// <param name="name">The name.</param><param name="body">The callback.</param><param name="nullBehavior">The NULL contract.</param><returns>The immutable function.</returns>
    /// <exception cref="ArgumentException">The name is default/invalid, or a CLR type is unsupported.</exception>
    /// <exception cref="ArgumentNullException">The callback is null.</exception>
    public static GraphScalarFunction Create<T1, TResult>(GraphFunctionName name, Func<T1, TResult> body, GraphNullBehavior nullBehavior = GraphNullBehavior.Strict)
        => new GraphTypedScalarFunction<T1, TResult>(name, body, nullBehavior);
    /// <summary>Creates a typed scalar with 2 argument(s).</summary>
    /// <typeparam name="T1">A query argument CLR type.</typeparam>
    /// <typeparam name="T2">A query argument CLR type.</typeparam>
    /// <typeparam name="TResult">The result CLR type.</typeparam>
    /// <param name="name">The name.</param><param name="body">The callback.</param><param name="nullBehavior">The NULL contract.</param><returns>The immutable function.</returns>
    /// <exception cref="ArgumentException">The name is default/invalid, or a CLR type is unsupported.</exception>
    /// <exception cref="ArgumentNullException">The callback is null.</exception>
    public static GraphScalarFunction Create<T1, T2, TResult>(GraphFunctionName name, Func<T1, T2, TResult> body, GraphNullBehavior nullBehavior = GraphNullBehavior.Strict)
        => new GraphTypedScalarFunction<T1, T2, TResult>(name, body, nullBehavior);
    /// <summary>Creates a typed scalar with 3 argument(s).</summary>
    /// <typeparam name="T1">A query argument CLR type.</typeparam>
    /// <typeparam name="T2">A query argument CLR type.</typeparam>
    /// <typeparam name="T3">A query argument CLR type.</typeparam>
    /// <typeparam name="TResult">The result CLR type.</typeparam>
    /// <param name="name">The name.</param><param name="body">The callback.</param><param name="nullBehavior">The NULL contract.</param><returns>The immutable function.</returns>
    /// <exception cref="ArgumentException">The name is default/invalid, or a CLR type is unsupported.</exception>
    /// <exception cref="ArgumentNullException">The callback is null.</exception>
    public static GraphScalarFunction Create<T1, T2, T3, TResult>(GraphFunctionName name, Func<T1, T2, T3, TResult> body, GraphNullBehavior nullBehavior = GraphNullBehavior.Strict)
        => new GraphTypedScalarFunction<T1, T2, T3, TResult>(name, body, nullBehavior);
    /// <summary>Creates a typed scalar with 4 argument(s).</summary>
    /// <typeparam name="T1">A query argument CLR type.</typeparam>
    /// <typeparam name="T2">A query argument CLR type.</typeparam>
    /// <typeparam name="T3">A query argument CLR type.</typeparam>
    /// <typeparam name="T4">A query argument CLR type.</typeparam>
    /// <typeparam name="TResult">The result CLR type.</typeparam>
    /// <param name="name">The name.</param><param name="body">The callback.</param><param name="nullBehavior">The NULL contract.</param><returns>The immutable function.</returns>
    /// <exception cref="ArgumentException">The name is default/invalid, or a CLR type is unsupported.</exception>
    /// <exception cref="ArgumentNullException">The callback is null.</exception>
    public static GraphScalarFunction Create<T1, T2, T3, T4, TResult>(GraphFunctionName name, Func<T1, T2, T3, T4, TResult> body, GraphNullBehavior nullBehavior = GraphNullBehavior.Strict)
        => new GraphTypedScalarFunction<T1, T2, T3, T4, TResult>(name, body, nullBehavior);
}
