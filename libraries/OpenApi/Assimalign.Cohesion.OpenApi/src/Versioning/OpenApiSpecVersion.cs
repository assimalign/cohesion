namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// Identifies an officially published OpenAPI Specification line that the Cohesion OpenApi model supports.
/// </summary>
/// <remarks>
/// Each member represents a minor line rather than a single patch release. The canonical patch emitted
/// for each line is published by <see cref="OpenApiVersionCapabilities.GetVersionString(OpenApiSpecVersion)"/>
/// (<c>3.0.4</c>, <c>3.1.2</c>, and <c>3.2.0</c> respectively).
/// </remarks>
public enum OpenApiSpecVersion
{
    /// <summary>The OpenAPI 3.0.x line (canonically emitted as <c>3.0.4</c>).</summary>
    V3_0,

    /// <summary>The OpenAPI 3.1.x line (canonically emitted as <c>3.1.2</c>).</summary>
    V3_1,

    /// <summary>The OpenAPI 3.2.x line (canonically emitted as <c>3.2.0</c>).</summary>
    V3_2
}
