using System;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>
/// A fluent builder for an <see cref="OpenApiCallback"/> — a map of out-of-band callback requests keyed
/// by a runtime expression that resolves to the callback URL.
/// </summary>
public sealed class OpenApiCallbackBuilder
{
    private readonly OpenApiCallback _callback = new();
    private readonly OpenApiSpecVersion _version;

    /// <summary>Initializes a new instance of the <see cref="OpenApiCallbackBuilder"/> class.</summary>
    /// <param name="version">The OpenAPI specification version the builder targets.</param>
    public OpenApiCallbackBuilder(OpenApiSpecVersion version)
    {
        _version = version;
    }

    /// <summary>Builds the configured <see cref="OpenApiCallback"/>.</summary>
    /// <returns>The configured <see cref="OpenApiCallback"/>.</returns>
    public OpenApiCallback Build() => _callback;

    /// <summary>Adds a callback path item keyed by a runtime expression.</summary>
    /// <param name="expression">The runtime expression, for example <c>{$request.body#/callbackUrl}</c>.</param>
    /// <param name="configure">Configures the callback path item.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiCallbackBuilder Expression(string expression, Action<OpenApiPathItemBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiPathItemBuilder(_version);
        configure(builder);
        _callback.PathItems[expression] = builder.Build();
        return this;
    }
}
