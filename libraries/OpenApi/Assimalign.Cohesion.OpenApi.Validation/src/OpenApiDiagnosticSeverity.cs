namespace Assimalign.Cohesion.OpenApi.Validation;

/// <summary>
/// The severity of an <see cref="OpenApiDiagnostic"/>.
/// </summary>
public enum OpenApiDiagnosticSeverity
{
    /// <summary>Informational guidance that does not affect validity.</summary>
    Info,

    /// <summary>A problem that does not by itself make the document invalid but should be addressed.</summary>
    Warning,

    /// <summary>A violation that makes the document invalid.</summary>
    Error
}
