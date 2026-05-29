using System;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// The area-scoped root exception for the Cohesion OpenApi family. Thrown for hard misuse of the
/// document model that cannot be expressed as a recoverable validation diagnostic.
/// </summary>
/// <remarks>
/// Recoverable problems found while inspecting a document — missing required fields, version
/// mismatches, unresolved references — are reported as <c>OpenApiDiagnostic</c> values by the
/// validation package rather than thrown. This exception is reserved for programming errors such as
/// constructing a structurally impossible element.
/// </remarks>
public class OpenApiException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="OpenApiException"/> class.</summary>
    public OpenApiException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="OpenApiException"/> class with a message.</summary>
    /// <param name="message">The message that describes the error.</param>
    public OpenApiException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="OpenApiException"/> class with a message and inner exception.</summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that caused this exception.</param>
    public OpenApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
