using System;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// Discriminates the scalar kind carried by an <see cref="OpenApiValueNode"/>.
/// </summary>
public enum OpenApiValueKind
{
    /// <summary>A null value.</summary>
    Null,

    /// <summary>A boolean value.</summary>
    Boolean,

    /// <summary>An integral number that fits in a 64-bit signed integer.</summary>
    Integer,

    /// <summary>A floating-point number.</summary>
    Double,

    /// <summary>A string value.</summary>
    String
}

/// <summary>
/// A scalar node in the OpenAPI value tree: a null, boolean, integer, floating-point, or string value.
/// </summary>
/// <remarks>
/// The scalar kind is preserved (an integer is never silently widened to a floating-point value) so
/// that round-trip serialization keeps the original numeric shape where the wire format allows it.
/// </remarks>
public sealed class OpenApiValueNode : OpenApiNode
{
    private readonly bool _boolean;
    private readonly long _integer;
    private readonly double _double;
    private readonly string? _string;

    private OpenApiValueNode(OpenApiValueKind kind, bool boolean, long integer, double @double, string? @string)
    {
        Kind = kind;
        _boolean = boolean;
        _integer = integer;
        _double = @double;
        _string = @string;
    }

    /// <summary>A shared instance representing the null value.</summary>
    public static readonly OpenApiValueNode Null = new(OpenApiValueKind.Null, false, 0L, 0d, null);

    /// <summary>Gets the scalar kind carried by this node.</summary>
    public OpenApiValueKind Kind { get; }

    /// <summary>Gets a value indicating whether this node represents the null value.</summary>
    public bool IsNull => Kind == OpenApiValueKind.Null;

    /// <summary>Creates a boolean value node.</summary>
    /// <param name="value">The boolean value.</param>
    /// <returns>A node of kind <see cref="OpenApiValueKind.Boolean"/>.</returns>
    public static OpenApiValueNode Boolean(bool value) => new(OpenApiValueKind.Boolean, value, 0L, 0d, null);

    /// <summary>Creates an integer value node.</summary>
    /// <param name="value">The integral value.</param>
    /// <returns>A node of kind <see cref="OpenApiValueKind.Integer"/>.</returns>
    public static OpenApiValueNode Integer(long value) => new(OpenApiValueKind.Integer, false, value, 0d, null);

    /// <summary>Creates a floating-point value node.</summary>
    /// <param name="value">The floating-point value.</param>
    /// <returns>A node of kind <see cref="OpenApiValueKind.Double"/>.</returns>
    public static OpenApiValueNode Double(double value) => new(OpenApiValueKind.Double, false, 0L, value, null);

    /// <summary>Creates a string value node.</summary>
    /// <param name="value">The string value.</param>
    /// <returns>A node of kind <see cref="OpenApiValueKind.String"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="value"/> is <see langword="null"/>.</exception>
    public static OpenApiValueNode String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(OpenApiValueKind.String, false, 0L, 0d, value);
    }

    /// <summary>Gets the boolean value.</summary>
    /// <returns>The wrapped boolean.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="Kind"/> is not <see cref="OpenApiValueKind.Boolean"/>.</exception>
    public bool GetBoolean() => Kind == OpenApiValueKind.Boolean
        ? _boolean
        : throw new InvalidOperationException($"Value node of kind '{Kind}' is not a boolean.");

    /// <summary>Gets the integral value.</summary>
    /// <returns>The wrapped integer.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="Kind"/> is not <see cref="OpenApiValueKind.Integer"/>.</exception>
    public long GetInteger() => Kind == OpenApiValueKind.Integer
        ? _integer
        : throw new InvalidOperationException($"Value node of kind '{Kind}' is not an integer.");

    /// <summary>Gets the numeric value as a floating-point number, widening an integer when necessary.</summary>
    /// <returns>The wrapped number.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="Kind"/> is neither <see cref="OpenApiValueKind.Double"/> nor <see cref="OpenApiValueKind.Integer"/>.</exception>
    public double GetDouble() => Kind switch
    {
        OpenApiValueKind.Double => _double,
        OpenApiValueKind.Integer => _integer,
        _ => throw new InvalidOperationException($"Value node of kind '{Kind}' is not a number.")
    };

    /// <summary>Gets the string value.</summary>
    /// <returns>The wrapped string.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="Kind"/> is not <see cref="OpenApiValueKind.String"/>.</exception>
    public string GetString() => Kind == OpenApiValueKind.String
        ? _string!
        : throw new InvalidOperationException($"Value node of kind '{Kind}' is not a string.");
}
