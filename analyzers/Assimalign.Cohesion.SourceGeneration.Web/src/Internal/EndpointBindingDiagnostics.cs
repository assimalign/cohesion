using Microsoft.CodeAnalysis;

namespace Assimalign.Cohesion.SourceGeneration.Web.Internal;

/// <summary>
/// The compiler diagnostics the endpoint-binding generator reports for typed <c>Map*</c> call sites it
/// cannot rewrite. Each one replaces what used to be a silent fall-through to the placeholder overload,
/// which throws at run time, so every descriptor is an error.
/// </summary>
internal static class EndpointBindingDiagnostics
{
    private const string Category = "Web";

    /// <summary>The handler argument is a delegate instance rather than a lambda or method group.</summary>
    internal static readonly DiagnosticDescriptor HandlerNotLambdaOrMethodGroup = new(
        "COHWEB0001",
        "Endpoint handler must be a lambda or a method group",
        "The handler passed to {0} is a delegate instance, so its parameters cannot be bound at compile time; pass a lambda expression or a method group instead",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>The handler returns a type the generated thunk cannot write.</summary>
    internal static readonly DiagnosticDescriptor UnsupportedReturnType = new(
        "COHWEB0002",
        "Endpoint handler return type is not supported",
        "The handler passed to {0} returns '{1}', which a typed endpoint does not support: {2}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>A handler parameter cannot be bound from its source, or cannot be named by generated code.</summary>
    internal static readonly DiagnosticDescriptor UnsupportedParameter = new(
        "COHWEB0003",
        "Endpoint handler parameter cannot be bound",
        "Parameter '{0}' of the handler passed to {1} cannot be bound: {2}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>More than one handler parameter binds from the request body.</summary>
    internal static readonly DiagnosticDescriptor MultipleBodyParameters = new(
        "COHWEB0004",
        "Endpoint handler binds more than one request body",
        "The handler passed to {0} binds both '{1}' and '{2}' from the request body, but a request has one body; bind a single model with [FromBody] and read the other values from the route, query string or headers",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>The handler binds a request-body model and form fields from the same request.</summary>
    internal static readonly DiagnosticDescriptor BodyAndFormParameters = new(
        "COHWEB0005",
        "Endpoint handler binds both a request body and form fields",
        "The handler passed to {0} binds '{1}' from the request body and '{2}' from form fields, but a request body is either a serialized model or a form; bind the values as [FromForm] fields, or bind the model alone",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>The handler's delegate type cannot be named, so generated code cannot invoke it.</summary>
    internal static readonly DiagnosticDescriptor UnnameableDelegateType = new(
        "COHWEB0006",
        "Endpoint handler delegate type cannot be named",
        "The handler passed to {0} cannot be invoked by generated code: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>The endpoint reads or writes serialized content, but the compilation cannot name Web.Serialization.</summary>
    internal static readonly DiagnosticDescriptor SerializationNotReferenced = new(
        "COHWEB0007",
        "Endpoint requires Assimalign.Cohesion.Web.Serialization",
        "The handler passed to {0} {1}, which requires the content-serialization registry in Assimalign.Cohesion.Web.Serialization; reference that package (Sdk.Web applications receive it through the App.Web shared framework)",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>Resolves a descriptor by its id for reporting a captured <see cref="DiagnosticInfo"/>.</summary>
    /// <param name="id">The diagnostic id.</param>
    /// <returns>The matching descriptor, or the unsupported-parameter descriptor as a fallback.</returns>
    internal static DiagnosticDescriptor GetDescriptor(string id) => id switch
    {
        "COHWEB0001" => HandlerNotLambdaOrMethodGroup,
        "COHWEB0002" => UnsupportedReturnType,
        "COHWEB0004" => MultipleBodyParameters,
        "COHWEB0005" => BodyAndFormParameters,
        "COHWEB0006" => UnnameableDelegateType,
        "COHWEB0007" => SerializationNotReferenced,
        _ => UnsupportedParameter
    };
}
