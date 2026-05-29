namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// Base type for the format-agnostic OpenAPI value tree. The tree represents arbitrary data such
/// as <c>example</c>, <c>default</c>, <c>enum</c>, and <c>const</c> values as well as the values of
/// specification extensions (<c>x-</c> fields).
/// </summary>
/// <remarks>
/// <para>
/// The node tree is intentionally independent of any serialization format. Serialization packages
/// render it to and from JSON or YAML; the canonical model itself never takes a dependency on a
/// concrete wire format. This is the seam that lets a YAML reader/writer be added without changing
/// the document model or the model-to-tree mapping.
/// </para>
/// <para>
/// Concrete node kinds are <see cref="OpenApiObjectNode"/>, <see cref="OpenApiArrayNode"/>, and
/// <see cref="OpenApiValueNode"/>. The set is closed: the constructor is private-protected so no
/// node kinds can be declared outside this assembly.
/// </para>
/// </remarks>
public abstract class OpenApiNode : IOpenApiElement
{
    private protected OpenApiNode()
    {
    }

    /// <summary>Creates a string value node, or a null node when <paramref name="value"/> is <see langword="null"/>.</summary>
    /// <param name="value">The string to wrap.</param>
    public static implicit operator OpenApiNode(string? value) =>
        value is null ? OpenApiValueNode.Null : OpenApiValueNode.String(value);

    /// <summary>Creates a boolean value node.</summary>
    /// <param name="value">The boolean to wrap.</param>
    public static implicit operator OpenApiNode(bool value) => OpenApiValueNode.Boolean(value);

    /// <summary>Creates an integer value node.</summary>
    /// <param name="value">The integer to wrap.</param>
    public static implicit operator OpenApiNode(long value) => OpenApiValueNode.Integer(value);

    /// <summary>Creates an integer value node.</summary>
    /// <param name="value">The integer to wrap.</param>
    public static implicit operator OpenApiNode(int value) => OpenApiValueNode.Integer(value);

    /// <summary>Creates a floating-point value node.</summary>
    /// <param name="value">The number to wrap.</param>
    public static implicit operator OpenApiNode(double value) => OpenApiValueNode.Double(value);
}
