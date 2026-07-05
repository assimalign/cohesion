using System;

namespace Assimalign.Cohesion.OpenApi.Fluent;

/// <summary>
/// A fluent builder for an <see cref="OpenApiLink"/> — a design-time link from a response to another
/// operation.
/// </summary>
public sealed class OpenApiLinkBuilder
{
    private readonly OpenApiLink _link = new();

    /// <summary>Builds the configured <see cref="OpenApiLink"/>.</summary>
    /// <returns>The configured <see cref="OpenApiLink"/>.</returns>
    public OpenApiLink Build() => _link;

    /// <summary>Sets the linked operation by its identifier. Mutually exclusive with <see cref="OperationRef"/>.</summary>
    /// <param name="operationId">The target operation identifier.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiLinkBuilder OperationId(string operationId)
    {
        ArgumentNullException.ThrowIfNull(operationId);
        _link.OperationId = operationId;
        return this;
    }

    /// <summary>Sets the linked operation by reference. Mutually exclusive with <see cref="OperationId"/>.</summary>
    /// <param name="operationRef">A relative or absolute URI reference to an operation.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiLinkBuilder OperationRef(string operationRef)
    {
        ArgumentNullException.ThrowIfNull(operationRef);
        _link.OperationRef = operationRef;
        return this;
    }

    /// <summary>Sets a description of the link. CommonMark may be used.</summary>
    /// <param name="description">The description.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiLinkBuilder Description(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        _link.Description = description;
        return this;
    }

    /// <summary>Adds a parameter to pass to the linked operation as a constant or runtime expression.</summary>
    /// <param name="name">The parameter name.</param>
    /// <param name="value">The parameter value or a runtime expression.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiLinkBuilder Parameter(string name, OpenApiNode value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        _link.Parameters[name] = value;
        return this;
    }

    /// <summary>Sets the request body to pass to the linked operation as a constant or runtime expression.</summary>
    /// <param name="value">The request body value or a runtime expression.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiLinkBuilder RequestBody(OpenApiNode value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _link.RequestBody = value;
        return this;
    }

    /// <summary>Sets the server to be used by the linked operation.</summary>
    /// <param name="url">The server URL.</param>
    /// <param name="description">An optional server description.</param>
    /// <returns>The same builder for chaining.</returns>
    public OpenApiLinkBuilder Server(string url, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        _link.Server = new OpenApiServer { Url = url, Description = description };
        return this;
    }
}
