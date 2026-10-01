using System;

namespace Assimalign.Cohesion.SourceGeneration.Web.Internal;

/// <summary>Where a handler parameter is bound from.</summary>
internal enum BindingSource
{
    Context,
    Cancellation,
    Feature,
    Route,
    Query,
    Header,
    Form,
    Body,

    /// <summary>
    /// A route value when the matched route captured one, otherwise the query string. Used when the call
    /// site cannot see the whole route template (a route-group endpoint, whose prefix is declared
    /// elsewhere, or a non-literal pattern), so a parameter the visible template does not name may still
    /// be a route parameter.
    /// </summary>
    RouteOrQuery
}

/// <summary>How a raw source value is converted to the parameter's type.</summary>
internal enum ConversionKind
{
    String,
    Parsable,
    Enum,
    NullableParsable,
    NullableEnum,
    Complex,
    Injection
}

/// <summary>The awaitable shape of the handler.</summary>
internal enum ReturnKind
{
    Task,
    ValueTask,
    Void
}

/// <summary>A single modeled handler parameter.</summary>
internal readonly record struct ParameterBinding(
    string DeclaredType,
    string CoreType,
    string FeatureType,
    BindingSource Source,
    ConversionKind Conversion,
    string Key,
    bool Required) : IEquatable<ParameterBinding>;

/// <summary>A modeled typed <c>Map*</c> call site the generator intercepts.</summary>
/// <remarks>
/// <c>RequiresAntiforgery</c> is set for a form-bound endpoint when the consuming compilation can name
/// the antiforgery requirement (<c>Assimalign.Cohesion.Web.Antiforgery.AntiforgeryMetadata</c>); the
/// interceptor then attaches it to the mapped route.
/// </remarks>
internal readonly record struct EndpointBinding(
    string InterceptsAttribute,
    string ReceiverType,
    bool HasMethodParameter,
    string MethodExpression,
    string DelegateType,
    ReturnKind Return,
    EquatableArray<ParameterBinding> Parameters,
    int BodyParameterIndex,
    bool UsesForm,
    bool RequiresAntiforgery) : IEquatable<EndpointBinding>;
