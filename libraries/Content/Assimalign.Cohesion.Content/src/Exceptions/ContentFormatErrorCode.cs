namespace Assimalign.Cohesion.Content;

/// <summary>Classifies malformed or unsupported content independently of diagnostic messages.</summary>
public enum ContentFormatErrorCode
{
    /// <summary>No classification was supplied, including by legacy constructors.</summary>
    Unspecified = 0,
    /// <summary>The input ended inside a structure.</summary>
    Truncated,
    /// <summary>The format forbids the structure, nesting, range, or offset.</summary>
    InvalidStructure,
    /// <summary>A field value is forbidden by the format specification.</summary>
    InvalidValue,
    /// <summary>A configured reader limit, such as buffered size or depth, was exceeded.</summary>
    LimitExceeded,
    /// <summary>The input is valid per the specification but outside the retained scope.</summary>
    Unsupported
}
