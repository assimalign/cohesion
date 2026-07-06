using System;

namespace Assimalign.Cohesion.OpenApi.Attributes;

/// <summary>
/// Declares that a method describes an OpenAPI operation. The HTTP method and path template locate the
/// operation; the remaining fields carry its documentation metadata.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class OpenApiOperationAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OpenApiOperationAttribute"/> class.
    /// </summary>
    /// <param name="method">The HTTP method the operation responds to.</param>
    /// <param name="path">The path template, for example <c>/pets/{id}</c>.</param>
    public OpenApiOperationAttribute(OperationType method, string path)
    {
        Method = method;
        Path = path;
    }

    /// <summary>Gets the HTTP method the operation responds to.</summary>
    public OperationType Method { get; }

    /// <summary>Gets the path template the operation is mounted at.</summary>
    public string Path { get; }

    /// <summary>Gets or sets the unique operation identifier.</summary>
    public string? OperationId { get; set; }

    /// <summary>Gets or sets a short summary of what the operation does.</summary>
    public string? Summary { get; set; }

    /// <summary>Gets or sets a verbose description of the operation. CommonMark may be used.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the grouping tags for the operation.</summary>
    public string[]? Tags { get; set; }

    /// <summary>Gets or sets a value indicating whether the operation is deprecated.</summary>
    public bool Deprecated { get; set; }
}
