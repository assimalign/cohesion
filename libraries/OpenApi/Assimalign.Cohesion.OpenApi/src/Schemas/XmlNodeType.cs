namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// The XML node type produced for a schema, per the <c>nodeType</c> field of the "XML Object" section of
/// the OpenAPI Specification (OpenAPI 3.2+).
/// </summary>
public enum XmlNodeType
{
    /// <summary>An XML element node.</summary>
    Element,

    /// <summary>An XML attribute node.</summary>
    Attribute,

    /// <summary>An XML text node.</summary>
    Text,

    /// <summary>An XML CDATA section node.</summary>
    Cdata,

    /// <summary>No XML node is produced for the schema.</summary>
    None
}
