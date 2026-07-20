using System;

namespace Assimalign.Cohesion.SourceGeneration.Web;

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
    Body
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
internal readonly record struct EndpointBinding(
    string InterceptsAttribute,
    string ReceiverType,
    bool HasMethodParameter,
    string MethodExpression,
    string DelegateType,
    ReturnKind Return,
    EquatableArray<ParameterBinding> Parameters,
    int BodyParameterIndex,
    bool UsesForm) : IEquatable<EndpointBinding>;
