using System;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>A group function whose fresh accumulator is driven by the engine.</summary>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class DocumentAggregateFunction : DocumentFunction
{
    /// <summary>Initializes aggregate metadata.</summary><param name="name">The name.</param><param name="argumentCount">The arity; zero uses star call syntax.</param><param name="returnType">The result type or Null for dynamic.</param><param name="nullBehavior">The unknown-input rule.</param><param name="argumentKinds">Accepted kinds.</param>
    /// <exception cref="ArgumentException">The name or kinds are invalid.</exception><exception cref="ArgumentOutOfRangeException">Metadata is invalid.</exception>
    protected DocumentAggregateFunction(DocumentFunctionName name, int argumentCount, DatabaseType returnType,
        DocumentNullBehavior nullBehavior = DocumentNullBehavior.PropagateMissingOrNull, params DocumentFunctionArgumentKind[] argumentKinds)
        : base(name, DocumentFunctionKind.Aggregate, argumentCount, argumentCount, returnType, nullBehavior, argumentKinds) { }

    /// <summary>Creates a fresh accumulator for one call and group.</summary><param name="context">The statement context.</param><returns>The exclusively owned accumulator.</returns>
    /// <exception cref="DatabaseException">The function failed, returned null or reused an accumulator.</exception><exception cref="OperationCanceledException">The statement was canceled.</exception>
    public DocumentAggregateAccumulator CreateAccumulator(scoped in DocumentFunctionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        try
        {
            var accumulator = CreateAccumulatorCore(in context);
            if (accumulator is null || !accumulator.TryAttach(this, context.CancellationToken))
            { throw new InvalidOperationException(ErrorMessages.FunctionAccumulatorInvalid); }
            return accumulator;
        }
        catch (Exception error) when (DocumentPlanningException.IsExternalFunctionFailure(error, context.CancellationToken))
        { throw DocumentPlanningException.FunctionFailed(Name, error); }
    }
    /// <summary>Creates one accumulator, never one returned before.</summary><param name="context">The call context.</param><returns>The new accumulator.</returns>
    protected abstract DocumentAggregateAccumulator CreateAccumulatorCore(scoped in DocumentFunctionContext context);
    /// <summary>Creates a typed aggregate of one argument; an empty admitted group returns NULL without finishing.</summary>
    /// <typeparam name="TState">The private state.</typeparam><typeparam name="T1">The input type.</typeparam><typeparam name="TResult">The output type.</typeparam><param name="name">The name.</param><param name="seed">Creates state per group.</param><param name="step">Combines one input.</param><param name="finish">Computes a result.</param><param name="nullBehavior">The unknown-input rule.</param><returns>The function.</returns>
    /// <exception cref="ArgumentNullException">A delegate is null.</exception><exception cref="NotSupportedException">A CLR value type is unsupported.</exception><exception cref="ArgumentException">The name or nullable-input contract is invalid.</exception>
    public static DocumentAggregateFunction Create<TState, T1, TResult>(DocumentFunctionName name, Func<TState> seed,
        Func<TState, T1, TState> step, Func<TState, TResult> finish,
        DocumentNullBehavior nullBehavior = DocumentNullBehavior.PropagateMissingOrNull)
        => new DocumentTypedAggregateFunction<TState, T1, TResult>(name, seed, step, finish, nullBehavior);
}
