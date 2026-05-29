namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// Describes how a parameter value is serialized. See the <c>style</c> field of the "Parameter Object"
/// section of the OpenAPI Specification.
/// </summary>
public enum ParameterStyle
{
    /// <summary>Path-style parameters defined by RFC 6570 (semicolon-prefixed).</summary>
    Matrix,

    /// <summary>Label-style parameters defined by RFC 6570 (dot-prefixed).</summary>
    Label,

    /// <summary>Simple comma-separated values; the default for path and header parameters.</summary>
    Simple,

    /// <summary>Form-style query expansion defined by RFC 6570; the default for query and cookie parameters.</summary>
    Form,

    /// <summary>Space-separated array values.</summary>
    SpaceDelimited,

    /// <summary>Pipe-separated array values.</summary>
    PipeDelimited,

    /// <summary>Nested object rendering using <c>param[property]</c> notation.</summary>
    DeepObject
}
