using Microsoft.CodeAnalysis;

namespace Assimalign.Cohesion.SourceGeneration.Web.Internal;

/// <summary>
/// The type checks behind the generator's handler diagnostics: whether generated code can name a
/// handler's parameter, result or delegate type, and whether a result is something an endpoint can
/// write. Each check returns the reason sentence a diagnostic embeds, or <see langword="null"/> when the
/// type is supported.
/// </summary>
internal static class HandlerTypeRules
{
    /// <summary>The display format diagnostics use for type names, matching the compiler's own messages.</summary>
    internal static readonly SymbolDisplayFormat MessageFormat = SymbolDisplayFormat.CSharpShortErrorMessageFormat;

    /// <summary>
    /// Describes why generated code cannot use <paramref name="type"/> as a handler parameter, or returns
    /// <see langword="null"/> when it can.
    /// </summary>
    /// <param name="type">The parameter's declared type.</param>
    /// <param name="compilation">The consuming compilation.</param>
    /// <param name="isErrorType">
    /// Set when the type, or a type it is built from, does not resolve. The compiler already reports that,
    /// so the caller abandons the call site without a diagnostic of its own.
    /// </param>
    /// <returns>The reason sentence, or <see langword="null"/>.</returns>
    internal static string? DescribeParameterTypeProblem(ITypeSymbol type, Compilation compilation, out bool isErrorType)
    {
        isErrorType = false;

        if (type.TypeKind == TypeKind.Dynamic)
        {
            return "'dynamic' needs run-time binding, which NativeAOT does not support; use a concrete type";
        }

        if (type.IsRefLikeType)
        {
            return $"'{type.ToDisplayString(MessageFormat)}' is a ref struct, which cannot hold a value bound from the request; use a string, an array or another regular type";
        }

        return DescribeNameProblem(type, compilation, out isErrorType);
    }

    /// <summary>
    /// Describes why an endpoint cannot write <paramref name="type"/> as the handler's (awaited) result,
    /// or returns <see langword="null"/> when it can.
    /// </summary>
    /// <param name="type">The value type the handler returns, after unwrapping <c>Task&lt;T&gt;</c> or <c>ValueTask&lt;T&gt;</c>.</param>
    /// <param name="compilation">The consuming compilation.</param>
    /// <param name="isAwaitedValue">
    /// Whether <paramref name="type"/> is the value of an awaited <c>Task&lt;T&gt;</c> or <c>ValueTask&lt;T&gt;</c>
    /// rather than the handler's direct return type, which changes how an awaitable value is explained.
    /// </param>
    /// <param name="isErrorType">Set when the type does not resolve (see <see cref="DescribeParameterTypeProblem"/>).</param>
    /// <returns>The reason sentence, or <see langword="null"/>.</returns>
    internal static string? DescribeResultProblem(ITypeSymbol type, Compilation compilation, bool isAwaitedValue, out bool isErrorType)
    {
        isErrorType = false;
        string display = type.ToDisplayString(MessageFormat);

        if (type.TypeKind == TypeKind.Dynamic)
        {
            return "'dynamic' needs run-time binding, which NativeAOT does not support; return a concrete type";
        }

        if (type.IsRefLikeType)
        {
            return $"'{display}' is a ref struct, which cannot be serialized; return an array or another regular type";
        }

        if (DescribeNameProblem(type, compilation, out isErrorType) is { } nameProblem)
        {
            return nameProblem;
        }

        if (IsAwaitable(type))
        {
            return isAwaitedValue
                ? $"the awaited value '{display}' is itself awaitable, so the endpoint would serialize it instead of awaiting it; await it inside the handler and return its result"
                : $"'{display}' is awaitable but is not Task, Task<T>, ValueTask or ValueTask<T>, so the endpoint would serialize it instead of awaiting it; await it inside the handler and return the value";
        }

        if (IsStream(type, compilation))
        {
            return $"'{display}' is a stream, which the content serializer cannot write; copy it to context.Response.Body inside the handler and return Task";
        }

        return null;
    }

    /// <summary>
    /// Describes why generated code cannot name a handler's own delegate type (for example a private
    /// delegate type created explicitly with <c>new</c>), or returns <see langword="null"/> when it can.
    /// </summary>
    /// <param name="delegateType">The handler's delegate type.</param>
    /// <param name="compilation">The consuming compilation.</param>
    /// <param name="isErrorType">Set when the type does not resolve (see <see cref="DescribeParameterTypeProblem"/>).</param>
    /// <returns>The reason sentence, or <see langword="null"/>.</returns>
    internal static string? DescribeDelegateTypeProblem(INamedTypeSymbol delegateType, Compilation compilation, out bool isErrorType)
    {
        if (DescribeNameProblem(delegateType, compilation, out isErrorType) is { } problem)
        {
            return $"its delegate type '{delegateType.ToDisplayString(MessageFormat)}' cannot be named: {problem}";
        }

        return null;
    }

    /// <summary>
    /// Describes why generated code — a file-local class in another namespace of the same assembly —
    /// cannot name <paramref name="type"/>: an anonymous type, a generic type parameter, a pointer, or a
    /// type that is private, protected or file-local. Type arguments and array element types are checked
    /// too.
    /// </summary>
    private static string? DescribeNameProblem(ITypeSymbol type, Compilation compilation, out bool isErrorType)
    {
        isErrorType = false;

        switch (type.TypeKind)
        {
            case TypeKind.Error:
                isErrorType = true;
                return null;

            case TypeKind.TypeParameter:
                return $"'{type.ToDisplayString(MessageFormat)}' is a generic type parameter, which generated code cannot name; map the endpoint with a concrete type";

            case TypeKind.Pointer:
            case TypeKind.FunctionPointer:
                return $"'{type.ToDisplayString(MessageFormat)}' is a pointer type, which cannot be bound or serialized; use a managed type";
        }

        if (type is IArrayTypeSymbol array)
        {
            return DescribeNameProblem(array.ElementType, compilation, out isErrorType);
        }

        if (type is not INamedTypeSymbol named)
        {
            return null;
        }

        if (named.IsAnonymousType)
        {
            return "anonymous types cannot be named by generated code or described to the source-generated serializer; use a named record or class";
        }

        if (!IsAccessible(named, compilation))
        {
            return $"'{named.ToDisplayString(MessageFormat)}' is not accessible to generated code because it is private, protected or file-local; make it internal or public";
        }

        foreach (ITypeSymbol argument in named.TypeArguments)
        {
            string? problem = DescribeNameProblem(argument, compilation, out isErrorType);
            if (problem is not null || isErrorType)
            {
                return problem;
            }
        }

        return null;
    }

    private static bool IsAccessible(INamedTypeSymbol type, Compilation compilation)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            if (current.IsFileLocal)
            {
                return false;
            }
        }

        return compilation.IsSymbolAccessibleWithin(type.OriginalDefinition, compilation.Assembly);
    }

    // An instance GetAwaiter() on the type or a base type makes it awaitable; extension GetAwaiter
    // methods are rare enough on response values to ignore.
    private static bool IsAwaitable(ITypeSymbol type)
    {
        for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            foreach (ISymbol member in current.GetMembers("GetAwaiter"))
            {
                if (member is IMethodSymbol { IsStatic: false, Parameters.Length: 0 })
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsStream(ITypeSymbol type, Compilation compilation)
    {
        INamedTypeSymbol? stream = compilation.GetTypeByMetadataName("System.IO.Stream");
        if (stream is null)
        {
            return false;
        }

        for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, stream))
            {
                return true;
            }
        }

        return false;
    }
}
