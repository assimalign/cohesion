using System;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>A native or application scalar function driven by the engine through one validated call.</summary>
/// <remarks>Callbacks must not call back into an engine: a nested operation on another session can wait indefinitely for a lock held by the invoking statement, without a lock-wait timeout or detected wait-for cycle. Same-session operations are refused before execution. Typed delegates do not receive the statement cancellation token; use a derived implementation and the borrowed context to observe it.</remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class DocumentScalarFunction : DocumentFunction
{
    /// <summary>Initializes scalar metadata.</summary>
    /// <param name="name">The name.</param><param name="minimumArguments">Minimum arity.</param><param name="maximumArguments">Maximum arity.</param><param name="returnType">The declared result type, or Null for dynamic.</param><param name="nullBehavior">The NULL/MISSING policy.</param><param name="argumentKinds">Accepted kinds, empty for any.</param>
    /// <exception cref="ArgumentException">The name or kinds are invalid.</exception><exception cref="ArgumentOutOfRangeException">Metadata is invalid.</exception>
    protected DocumentScalarFunction(DocumentFunctionName name, int minimumArguments, int maximumArguments, DatabaseType returnType,
        DocumentNullBehavior nullBehavior = DocumentNullBehavior.PropagateMissingOrNull, params DocumentFunctionArgumentKind[] argumentKinds)
        : base(name, DocumentFunctionKind.Scalar, minimumArguments, maximumArguments, returnType, nullBehavior, argumentKinds) { }

    /// <summary>Invokes the scalar, checking metadata, cancellation and its result.</summary><param name="arguments">The borrowed call arguments.</param><returns>The result.</returns>
    /// <exception cref="ArgumentException">The argument count is invalid.</exception><exception cref="DatabaseException">The function failed, preserving database failures and coding external faults as CD05017.</exception><exception cref="OperationCanceledException">The statement was canceled.</exception>
    public DocumentValue Invoke(scoped in DocumentArguments arguments)
    {
        arguments.Context.CancellationToken.ThrowIfCancellationRequested();
        CheckArity(arguments.Count);
        if (NullBehavior == DocumentNullBehavior.PropagateMissingOrNull && Propagate(in arguments, out var propagated)) { return propagated; }
        for (int index = 0; index < arguments.Count; index++)
        {
            if (!arguments[index].IsNull && !arguments[index].IsMissing &&
                !AcceptsArgument(index, DocumentExpressionEvaluator.FunctionKind(arguments[index].Value))) { return DocumentValue.Null; }
        }
        try { return CheckResult(InvokeCore(in arguments)); }
        catch (Exception error) when (DocumentPlanningException.IsExternalFunctionFailure(error, arguments.Context.CancellationToken))
        { throw DocumentPlanningException.FunctionFailed(Name, error); }
    }

    /// <summary>Computes one result; called only through Invoke.</summary><param name="arguments">The borrowed arguments.</param><returns>The result.</returns>
    protected abstract DocumentValue InvokeCore(scoped in DocumentArguments arguments);

    /// <summary>Creates a typed scalar of one argument.</summary><typeparam name="T1">The input CLR type.</typeparam><typeparam name="TResult">The result CLR type.</typeparam><param name="name">The name.</param><param name="body">The thread-safe implementation.</param><param name="nullBehavior">The NULL/MISSING policy.</param><returns>The function to register.</returns><exception cref="ArgumentNullException">The body is null.</exception><exception cref="NotSupportedException">A CLR type is unsupported.</exception><exception cref="ArgumentException">A strictness/type combination or name is invalid.</exception>
    public static DocumentScalarFunction Create<T1, TResult>(DocumentFunctionName name, Func<T1, TResult> body,
        DocumentNullBehavior nullBehavior = DocumentNullBehavior.PropagateMissingOrNull)
        => new DocumentTypedScalarFunction<T1, TResult>(name, body, nullBehavior);
    /// <summary>Creates a typed scalar of two arguments.</summary><typeparam name="T1">First CLR type.</typeparam><typeparam name="T2">Second CLR type.</typeparam><typeparam name="TResult">Result CLR type.</typeparam><param name="name">The name.</param><param name="body">The implementation.</param><param name="nullBehavior">The NULL/MISSING policy.</param><returns>The function.</returns><exception cref="ArgumentNullException">The body is null.</exception><exception cref="NotSupportedException">A CLR type is unsupported.</exception><exception cref="ArgumentException">A name or type is invalid.</exception>
    public static DocumentScalarFunction Create<T1, T2, TResult>(DocumentFunctionName name, Func<T1, T2, TResult> body,
        DocumentNullBehavior nullBehavior = DocumentNullBehavior.PropagateMissingOrNull)
        => new DocumentTypedScalarFunction<T1, T2, TResult>(name, body, nullBehavior);
    /// <summary>Creates a typed scalar of three arguments.</summary><typeparam name="T1">First CLR type.</typeparam><typeparam name="T2">Second CLR type.</typeparam><typeparam name="T3">Third CLR type.</typeparam><typeparam name="TResult">Result CLR type.</typeparam><param name="name">The name.</param><param name="body">The implementation.</param><param name="nullBehavior">The NULL/MISSING policy.</param><returns>The function.</returns><exception cref="ArgumentNullException">The body is null.</exception><exception cref="NotSupportedException">A CLR type is unsupported.</exception><exception cref="ArgumentException">A name or type is invalid.</exception>
    public static DocumentScalarFunction Create<T1, T2, T3, TResult>(DocumentFunctionName name, Func<T1, T2, T3, TResult> body,
        DocumentNullBehavior nullBehavior = DocumentNullBehavior.PropagateMissingOrNull)
        => new DocumentTypedScalarFunction<T1, T2, T3, TResult>(name, body, nullBehavior);
    /// <summary>Creates a typed scalar of four arguments.</summary><typeparam name="T1">First CLR type.</typeparam><typeparam name="T2">Second CLR type.</typeparam><typeparam name="T3">Third CLR type.</typeparam><typeparam name="T4">Fourth CLR type.</typeparam><typeparam name="TResult">Result CLR type.</typeparam><param name="name">The name.</param><param name="body">The implementation.</param><param name="nullBehavior">The NULL/MISSING policy.</param><returns>The function.</returns><exception cref="ArgumentNullException">The body is null.</exception><exception cref="NotSupportedException">A CLR type is unsupported.</exception><exception cref="ArgumentException">A name or type is invalid.</exception>
    public static DocumentScalarFunction Create<T1, T2, T3, T4, TResult>(DocumentFunctionName name, Func<T1, T2, T3, T4, TResult> body,
        DocumentNullBehavior nullBehavior = DocumentNullBehavior.PropagateMissingOrNull)
        => new DocumentTypedScalarFunction<T1, T2, T3, T4, TResult>(name, body, nullBehavior);
}
