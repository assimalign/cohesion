using System;
using System.Globalization;

namespace Assimalign.Cohesion.Content.Yaml;

/// <summary>
/// A scalar node: a single value with a resolved core-schema <see cref="Kind"/> and a presentation
/// <see cref="Style"/>.
/// </summary>
public sealed class YamlScalar : YamlNode
{
    /// <summary>
    /// Initializes a new plain string scalar whose kind is resolved by the core schema, so
    /// <c>new YamlScalar("true")</c> is a boolean and <c>new YamlScalar("text")</c> is a string.
    /// </summary>
    /// <param name="value">The scalar content.</param>
    public YamlScalar(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
        Kind = YamlCoreSchema.Resolve(value);
    }

    /// <summary>Initializes a boolean scalar.</summary>
    /// <param name="value">The boolean value.</param>
    public YamlScalar(bool value)
    {
        Value = value ? "true" : "false";
        Kind = YamlScalarKind.Boolean;
    }

    /// <summary>Initializes an integer scalar.</summary>
    /// <param name="value">The integral value.</param>
    public YamlScalar(long value)
    {
        Value = value.ToString(CultureInfo.InvariantCulture);
        Kind = YamlScalarKind.Integer;
    }

    /// <summary>Initializes a floating-point scalar.</summary>
    /// <param name="value">The floating-point value.</param>
    public YamlScalar(double value)
    {
        Value = double.IsNaN(value) ? ".nan"
            : double.IsPositiveInfinity(value) ? ".inf"
            : double.IsNegativeInfinity(value) ? "-.inf"
            : value.ToString("R", CultureInfo.InvariantCulture);
        Kind = YamlScalarKind.Float;
    }

    internal YamlScalar(string value, YamlScalarKind kind, YamlScalarStyle style)
    {
        Value = value;
        Kind = kind;
        Style = style;
    }

    /// <summary>Gets a new null scalar.</summary>
    public static YamlScalar Null => new(string.Empty, YamlScalarKind.Null, YamlScalarStyle.Plain);

    /// <summary>
    /// Creates a string scalar regardless of what the core schema would resolve the text to; the
    /// emitter quotes it when necessary to preserve the string kind.
    /// </summary>
    /// <param name="value">The string content.</param>
    /// <returns>A scalar of kind <see cref="YamlScalarKind.String"/>.</returns>
    public static YamlScalar FromString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new YamlScalar(value, YamlScalarKind.String, YamlScalarStyle.Plain);
    }

    /// <summary>Gets or sets the scalar content, after any unescaping, folding, and chomping.</summary>
    public string Value { get; set; }

    /// <summary>Gets or sets the resolved core-schema kind of the scalar.</summary>
    public YamlScalarKind Kind { get; set; }

    /// <summary>Gets or sets the presentation style of the scalar.</summary>
    public YamlScalarStyle Style { get; set; }

    /// <summary>Gets a value indicating whether the scalar is the null value.</summary>
    public bool IsNull => Kind == YamlScalarKind.Null;

    /// <summary>Gets the boolean value of a <see cref="YamlScalarKind.Boolean"/> scalar.</summary>
    /// <returns>The parsed boolean.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="Kind"/> is not <see cref="YamlScalarKind.Boolean"/>.</exception>
    public bool GetBoolean() => Kind == YamlScalarKind.Boolean
        ? Value is "true" or "True" or "TRUE"
        : throw new InvalidOperationException($"Scalar of kind '{Kind}' is not a boolean.");

    /// <summary>Gets the integral value of a <see cref="YamlScalarKind.Integer"/> scalar.</summary>
    /// <returns>The parsed integer.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="Kind"/> is not <see cref="YamlScalarKind.Integer"/>.</exception>
    public long GetInteger() => Kind == YamlScalarKind.Integer
        ? YamlCoreSchema.ParseInteger(Value)
        : throw new InvalidOperationException($"Scalar of kind '{Kind}' is not an integer.");

    /// <summary>Gets the numeric value of a <see cref="YamlScalarKind.Float"/> or <see cref="YamlScalarKind.Integer"/> scalar.</summary>
    /// <returns>The parsed number.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the scalar is not numeric.</exception>
    public double GetDouble() => Kind switch
    {
        YamlScalarKind.Float => YamlCoreSchema.ParseFloat(Value),
        YamlScalarKind.Integer => YamlCoreSchema.ParseInteger(Value),
        _ => throw new InvalidOperationException($"Scalar of kind '{Kind}' is not a number.")
    };

    /// <inheritdoc/>
    public override string ToString() => Value;
}
