using System;
using System.Collections.Generic;
using System.Linq;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>Immutable function metadata shared by native and developer functions.</summary>
/// <remarks>Callbacks must not call back into an engine: a nested operation on another session can wait indefinitely for a lock held by the invoking statement, without a lock-wait timeout or detected wait-for cycle. Same-session operations are refused before execution. Typed delegates do not receive the statement cancellation token; use a derived implementation and the borrowed context to observe it.</remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class GraphFunction
{
    private protected GraphFunction(GraphFunctionName name, IEnumerable<GraphFunctionType> parameters, GraphFunctionType returnType, GraphNullBehavior nullBehavior, bool scalar = false)
    {
        if (!GraphFunctionName.IsValid(name.ToString(), out string? error)) { throw new ArgumentException(error, nameof(name)); }
        ArgumentNullException.ThrowIfNull(parameters); ArgumentNullException.ThrowIfNull(returnType);
        var copy = parameters.ToArray();
        if (copy.Any(type => type is null)) { throw new ArgumentException(ErrorMessages.FunctionTypeUnsupported, nameof(parameters)); }
        if (!Enum.IsDefined(nullBehavior)) { throw new ArgumentOutOfRangeException(nameof(nullBehavior)); }
        Name = name; Parameters = Array.AsReadOnly(copy); DeclaredReturnType = returnType;
        ReturnType = scalar && nullBehavior == GraphNullBehavior.ReturnsNullOnNullInput && copy.Length > 0 ? returnType.AsNullable() : returnType; NullBehavior = nullBehavior;
    }
    /// <summary>Gets the case-insensitive function identifier.</summary>
    public GraphFunctionName Name { get; }
    /// <summary>Gets immutable parameter metadata.</summary>
    public IReadOnlyList<GraphFunctionType> Parameters { get; }
    internal GraphFunctionType DeclaredReturnType { get; }
    /// <summary>Gets immutable invocation result metadata, including strict scalar NULL short-circuiting, used before execution or writes.</summary>
    public GraphFunctionType ReturnType { get; }
    /// <summary>Gets the NULL input contract.</summary>
    public GraphNullBehavior NullBehavior { get; }
    internal virtual bool AcceptsArity(int count) => count == Parameters.Count;
    internal virtual bool IsNative => false;
}
