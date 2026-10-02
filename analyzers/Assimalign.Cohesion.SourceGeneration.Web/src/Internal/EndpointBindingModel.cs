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

/// <summary>The shape of the handler's return: what the thunk awaits, and whether a value comes back.</summary>
internal enum ReturnKind
{
    Task,
    ValueTask,
    Void,

    /// <summary>A value returned synchronously (<c>T</c>).</summary>
    Value,

    /// <summary>A value returned through <c>Task&lt;T&gt;</c>.</summary>
    TaskOfValue,

    /// <summary>A value returned through <c>ValueTask&lt;T&gt;</c>.</summary>
    ValueTaskOfValue
}

/// <summary>How the thunk writes the value the handler returned.</summary>
internal enum ResponseKind
{
    /// <summary>The handler returns no value; it writes the response itself.</summary>
    None,

    /// <summary>A <c>string</c>, written as <c>text/plain; charset=utf-8</c> without negotiation.</summary>
    Text,

    /// <summary>Any other value, written through the content-serialization registry with negotiation.</summary>
    Serialized
}

/// <summary>Whether the returned value can be <see langword="null"/>, and how the thunk tests it.</summary>
internal enum ResultNullCheck
{
    /// <summary>A non-nullable value type: never <see langword="null"/>.</summary>
    None,

    /// <summary>A reference type: tested with <c>is null</c>.</summary>
    Reference,

    /// <summary>A <c>Nullable&lt;T&gt;</c>: tested with <c>is null</c> and written as its underlying value.</summary>
    NullableValue
}

/// <summary>A single modeled handler parameter.</summary>
/// <remarks>
/// <c>DescribedType</c> is the declared type without nullable reference annotations, the form a
/// <c>typeof(...)</c> in the endpoint's description metadata accepts.
/// </remarks>
internal readonly record struct ParameterBinding(
    string DeclaredType,
    string CoreType,
    string FeatureType,
    BindingSource Source,
    ConversionKind Conversion,
    string Key,
    bool Required,
    string DescribedType) : IEquatable<ParameterBinding>;

/// <summary>A modeled typed <c>Map*</c> call site the generator intercepts.</summary>
/// <remarks>
/// <para>
/// <c>DelegateType</c> is the handler's own delegate type (its natural <c>Func</c>/<c>Action</c> type, or
/// an explicitly created named delegate type), so the cast in the interceptor always matches the runtime
/// delegate. <c>ResultType</c> is the declared type of the returned (awaited) value and <c>WrittenType</c>
/// the type argument the serialized write uses: the result type without its top-level nullable annotation,
/// and the underlying type of a <c>Nullable&lt;T&gt;</c>. Both are empty when the handler returns no value.
/// </para>
/// <para>
/// <c>DescribedResultType</c> is the written type without nullable reference annotations, for the
/// <c>typeof(...)</c> in the endpoint's response description, and <c>DescribesNoContent</c> records that
/// the declared result admits <see langword="null"/>, so the description lists the <c>204</c>.
/// </para>
/// <para>
/// <c>RequiresAntiforgery</c> is set for a form-bound endpoint when the consuming compilation can name
/// the antiforgery requirement (<c>Assimalign.Cohesion.Web.Antiforgery.AntiforgeryMetadata</c>); the
/// interceptor then attaches it to the mapped route.
/// </para>
/// </remarks>
internal readonly record struct EndpointBinding(
    string InterceptsAttribute,
    string ReceiverType,
    bool HasMethodParameter,
    string MethodExpression,
    string DelegateType,
    ReturnKind Return,
    string ResultType,
    ResponseKind Response,
    string WrittenType,
    ResultNullCheck NullCheck,
    string DescribedResultType,
    bool DescribesNoContent,
    EquatableArray<ParameterBinding> Parameters,
    int BodyParameterIndex,
    bool UsesForm,
    bool RequiresAntiforgery) : IEquatable<EndpointBinding>;

/// <summary>
/// The outcome of analyzing one typed <c>Map*</c> call site: the binding to emit, or the diagnostics that
/// explain why the call site cannot be rewritten. Exactly one of the two is populated.
/// </summary>
internal readonly record struct EndpointAnalysis(
    EndpointBinding? Binding,
    EquatableArray<DiagnosticInfo> Diagnostics) : IEquatable<EndpointAnalysis>;
