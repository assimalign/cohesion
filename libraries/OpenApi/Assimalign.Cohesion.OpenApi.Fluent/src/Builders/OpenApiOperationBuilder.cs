using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>
/// A fluent builder for an <see cref="OpenApiOperation"/>.
/// </summary>
public sealed class OpenApiOperationBuilder
{
    private readonly OpenApiOperation _operation = new();
    private readonly OpenApiSpecVersion _version;

    /// <summary>Initializes a new instance of the <see cref="OpenApiOperationBuilder"/> class.</summary>
    /// <param name="version">The OpenAPI specification version the builder targets.</param>
    public OpenApiOperationBuilder(OpenApiSpecVersion version)
    {
        _version = version;
    }

    /// <summary>Builds the configured <see cref="OpenApiOperation"/>.</summary>
    /// <returns>The built operation.</returns>
    public OpenApiOperation Build() => _operation;

    /// <summary>Sets the operation identifier.</summary>
    /// <param name="operationId">The unique operation identifier.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiOperationBuilder OperationId(string operationId)
    {
        ArgumentNullException.ThrowIfNull(operationId);
        _operation.OperationId = operationId;
        return this;
    }

    /// <summary>Sets a short summary of the operation.</summary>
    /// <param name="summary">The summary.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiOperationBuilder Summary(string summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        _operation.Summary = summary;
        return this;
    }

    /// <summary>Sets a verbose description of the operation.</summary>
    /// <param name="description">The description. CommonMark may be used.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiOperationBuilder Description(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        _operation.Description = description;
        return this;
    }

    /// <summary>Adds a grouping tag to the operation.</summary>
    /// <param name="tag">The tag name.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiOperationBuilder Tag(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        _operation.Tags.Add(tag);
        return this;
    }

    /// <summary>Marks the operation as deprecated.</summary>
    /// <param name="deprecated">Whether the operation is deprecated.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiOperationBuilder Deprecated(bool deprecated = true)
    {
        _operation.Deprecated = deprecated;
        return this;
    }

    /// <summary>Adds a parameter to the operation.</summary>
    /// <param name="name">The parameter name.</param>
    /// <param name="location">The parameter location.</param>
    /// <param name="configure">Configures the parameter.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiOperationBuilder Parameter(string name, ParameterLocation location, Action<OpenApiParameterBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);

        if (location == ParameterLocation.Querystring)
        {
            OpenApiBuildGuard.Require(_version, OpenApiFeature.ParameterQuerystringLocation, "The 'querystring' parameter location");
        }

        var parameter = new OpenApiParameter { Name = name, In = location, Required = location == ParameterLocation.Path };
        var builder = new OpenApiParameterBuilder(parameter, _version);
        configure(builder);
        _operation.Parameters.Add(builder.Build());
        return this;
    }

    /// <summary>Adds a parameter to the operation by reference.</summary>
    /// <param name="reference">The <c>$ref</c> value, for example <c>#/components/parameters/id</c>.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiOperationBuilder ParameterReference(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        _operation.Parameters.Add(new OpenApiParameter { Reference = new OpenApiReference { Ref = reference } });
        return this;
    }

    /// <summary>Sets the request body.</summary>
    /// <param name="configure">Configures the request body.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiOperationBuilder RequestBody(Action<OpenApiRequestBodyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiRequestBodyBuilder(_version);
        configure(builder);
        _operation.RequestBody = builder.Build();
        return this;
    }

    /// <summary>Adds a response for a status code, range, or <c>default</c>.</summary>
    /// <param name="statusCode">The response key.</param>
    /// <param name="configure">Configures the response.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiOperationBuilder Response(string statusCode, Action<OpenApiResponseBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(statusCode);
        ArgumentNullException.ThrowIfNull(configure);
        _operation.Responses ??= new OpenApiResponses();
        var builder = new OpenApiResponseBuilder(_version);
        configure(builder);
        _operation.Responses.Items[statusCode] = builder.Build();
        return this;
    }

    /// <summary>Adds a security requirement for this operation.</summary>
    /// <param name="scheme">The security scheme name.</param>
    /// <param name="scopes">The required scopes, if any.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiOperationBuilder Security(string scheme, params string[] scopes)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        var requirement = new OpenApiSecurityRequirement();
        requirement.Schemes[scheme] = new List<string>(scopes ?? []);
        _operation.Security.Add(requirement);
        return this;
    }

    /// <summary>Adds an out-of-band callback related to this operation.</summary>
    /// <param name="name">The callback name.</param>
    /// <param name="configure">Configures the callback.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiOperationBuilder Callback(string name, Action<OpenApiCallbackBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new OpenApiCallbackBuilder(_version);
        configure(builder);
        _operation.Callbacks[name] = builder.Build();
        return this;
    }

    /// <summary>Adds an alternative server that services this operation.</summary>
    /// <param name="url">The server URL.</param>
    /// <param name="description">An optional server description.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiOperationBuilder Server(string url, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        _operation.Servers.Add(new OpenApiServer { Url = url, Description = description });
        return this;
    }

    /// <summary>Sets additional external documentation for this operation.</summary>
    /// <param name="url">The documentation URL.</param>
    /// <param name="description">An optional description. CommonMark may be used.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiOperationBuilder ExternalDocs(string url, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        _operation.ExternalDocs = new OpenApiExternalDocumentation { Url = url, Description = description };
        return this;
    }

    /// <summary>Sets a specification extension (an <c>x-</c> field).</summary>
    /// <param name="name">The extension name, including the <c>x-</c> prefix.</param>
    /// <param name="value">The extension value.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiOperationBuilder Extension(string name, OpenApiNode value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        _operation.Extensions[name] = value;
        return this;
    }
}
