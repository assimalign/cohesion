using System;

namespace Assimalign.Cohesion.OpenApi.Attributes;

/// <summary>
/// Describes one response of an OpenAPI operation, keyed by status code, status-code range, or
/// <c>default</c>. The response schema is named either by a <see cref="ModelType"/> (resolved to a
/// component reference by type name) or by an explicit <see cref="SchemaReference"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class OpenApiResponseAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OpenApiResponseAttribute"/> class for a status code.
    /// </summary>
    /// <param name="statusCode">The HTTP status code.</param>
    public OpenApiResponseAttribute(int statusCode)
    {
        StatusCode = statusCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenApiResponseAttribute"/> class for a status-code
    /// range or the <c>default</c> response.
    /// </summary>
    /// <param name="statusCode">The response key, for example <c>2XX</c> or <c>default</c>.</param>
    public OpenApiResponseAttribute(string statusCode)
    {
        StatusCode = statusCode;
    }

    /// <summary>Gets the response key.</summary>
    public string StatusCode { get; }

    /// <summary>Gets or sets a description of the response. CommonMark may be used.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the media type of the response body, when it has content.</summary>
    public string? ContentType { get; set; }

    /// <summary>Gets or sets the CLR model type whose name resolves the response schema component. Mutually exclusive with <see cref="SchemaReference"/>.</summary>
    public Type? ModelType { get; set; }

    /// <summary>Gets or sets an explicit schema reference, for example <c>#/components/schemas/Pet</c>. Mutually exclusive with <see cref="ModelType"/>.</summary>
    public string? SchemaReference { get; set; }
}
