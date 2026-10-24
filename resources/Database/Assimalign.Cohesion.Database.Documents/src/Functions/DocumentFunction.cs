using System;
using System.Collections.Generic;
using System.Text.Json;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>Immutable metadata shared by native and application document functions.</summary>
/// <remarks>One instance serves all sessions; implementations must be stateless or thread-safe. Per-group state belongs to an accumulator.</remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class DocumentFunction
{
    private readonly DocumentFunctionArgumentKind[] _argumentKinds;

    /// <summary>Initializes immutable function metadata.</summary>
    /// <param name="name">The function name.</param><param name="kind">The variant.</param><param name="minimumArguments">Minimum arity.</param><param name="maximumArguments">Maximum arity, or Int32.MaxValue.</param><param name="returnType">The result storage type; Null permits a dynamically typed result.</param><param name="nullBehavior">The NULL/MISSING policy.</param><param name="argumentKinds">Accepted nonempty valued-kind masks; the last mask repeats.</param>
    /// <exception cref="ArgumentException">The name is default or an argument kind is unsupported.</exception><exception cref="ArgumentOutOfRangeException">Metadata is outside its supported range.</exception>
    private protected DocumentFunction(DocumentFunctionName name, DocumentFunctionKind kind, int minimumArguments, int maximumArguments,
        DatabaseType returnType, DocumentNullBehavior nullBehavior, ReadOnlySpan<DocumentFunctionArgumentKind> argumentKinds)
    {
        if (!DocumentFunctionName.IsValid(name.ToString(), out var error)) { throw new ArgumentException(error, nameof(name)); }
        ArgumentOutOfRangeException.ThrowIfNegative(minimumArguments);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumArguments, minimumArguments);
        if (nullBehavior is not (DocumentNullBehavior.PropagateMissingOrNull or DocumentNullBehavior.CalledOnNullOrMissingInput))
        { throw new ArgumentOutOfRangeException(nameof(nullBehavior), nullBehavior, ErrorMessages.FunctionMetadataInvalid); }
        if (returnType is not (DatabaseType.Null or DatabaseType.Boolean or DatabaseType.Int64 or DatabaseType.Decimal or DatabaseType.String or DatabaseType.Json))
        { throw new ArgumentOutOfRangeException(nameof(returnType), returnType, ErrorMessages.FunctionMetadataInvalid); }
        foreach (var argument in argumentKinds)
        {
            if (argument == DocumentFunctionArgumentKind.None || (argument & ~DocumentFunctionArgumentKind.Any) != DocumentFunctionArgumentKind.None)
            { throw new ArgumentException(ErrorMessages.FunctionMetadataInvalid, nameof(argumentKinds)); }
        }
        Name = name;
        Kind = kind;
        MinimumArguments = minimumArguments;
        MaximumArguments = maximumArguments;
        ReturnType = returnType;
        NullBehavior = nullBehavior;
        _argumentKinds = argumentKinds.ToArray();
        ArgumentKinds = Array.AsReadOnly(_argumentKinds);
    }
    /// <summary>Gets the name.</summary>
    public DocumentFunctionName Name { get; }
    /// <summary>Gets the variant.</summary>
    public DocumentFunctionKind Kind { get; }
    /// <summary>Gets the minimum arity.</summary>
    public int MinimumArguments { get; }
    /// <summary>Gets the maximum arity.</summary>
    public int MaximumArguments { get; }
    /// <summary>Gets the declared result type; Null means dynamic.</summary>
    public DatabaseType ReturnType { get; }
    /// <summary>Gets the NULL/MISSING policy.</summary>
    public DocumentNullBehavior NullBehavior { get; }
    /// <summary>Gets accepted valued-kind masks, empty for Any; the final mask repeats for trailing arguments.</summary>
    public IReadOnlyList<DocumentFunctionArgumentKind> ArgumentKinds { get; }
    internal bool AcceptsArity(int count) => count >= MinimumArguments && count <= MaximumArguments;
    internal bool AcceptsArgument(int index, JsonValueKind kind)
    {
        if (_argumentKinds.Length == 0) { return true; }
        var expected = _argumentKinds[Math.Min(index, _argumentKinds.Length - 1)];
        return (expected & ClassifyArgumentKind(kind)) != DocumentFunctionArgumentKind.None;
    }
    internal static DocumentFunctionArgumentKind ClassifyArgumentKind(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Undefined => DocumentFunctionArgumentKind.Any,
        JsonValueKind.True or JsonValueKind.False => DocumentFunctionArgumentKind.Boolean,
        JsonValueKind.Number => DocumentFunctionArgumentKind.Number,
        JsonValueKind.String => DocumentFunctionArgumentKind.String,
        JsonValueKind.Array => DocumentFunctionArgumentKind.Array,
        JsonValueKind.Object => DocumentFunctionArgumentKind.Object,
        _ => DocumentFunctionArgumentKind.None,
    };
    internal bool Propagate(scoped in DocumentArguments arguments, out DocumentValue value)
    {
        bool hasNull = false;
        for (int index = 0; index < arguments.Count; index++)
        {
            if (arguments[index].IsMissing) { value = DocumentValue.Missing; return true; }
            hasNull |= arguments[index].IsNull;
        }
        value = default;
        return hasNull;
    }
    internal DocumentValue CheckResult(DocumentValue value)
    {
        if (!value.IsNull && !value.IsMissing && ReturnType != DatabaseType.Null && value.Type != ReturnType)
        { throw DocumentPlanningException.FunctionFailed(Name, new InvalidCastException(ErrorMessages.FunctionResultInvalid)); }
        return value;
    }
    internal void CheckArity(int count)
    {
        if (!AcceptsArity(count)) { throw new ArgumentException(ErrorMessages.FunctionArgumentsInvalid, nameof(count)); }
    }
}
