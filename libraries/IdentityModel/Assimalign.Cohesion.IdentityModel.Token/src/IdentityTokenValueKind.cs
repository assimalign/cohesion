namespace Assimalign.Cohesion.IdentityModel.Token;

/// <summary>
/// Represents the normalized shape of a token claim value.
/// </summary>
public enum IdentityTokenValueKind
{
    /// <summary>
    /// The claim value shape is not known.
    /// </summary>
    Undefined,

    /// <summary>
    /// The claim value is a string.
    /// </summary>
    String,

    /// <summary>
    /// The claim value is a Boolean.
    /// </summary>
    Boolean,

    /// <summary>
    /// The claim value is an integer.
    /// </summary>
    Integer,

    /// <summary>
    /// The claim value is a decimal or floating-point number.
    /// </summary>
    Decimal,

    /// <summary>
    /// The claim value is a date or time.
    /// </summary>
    DateTime,

    /// <summary>
    /// The claim value is a collection.
    /// </summary>
    Array,

    /// <summary>
    /// The claim value is a complex object.
    /// </summary>
    Object,

    /// <summary>
    /// The claim value is binary data.
    /// </summary>
    Binary,

    /// <summary>
    /// The claim value is explicitly null.
    /// </summary>
    Null
}
