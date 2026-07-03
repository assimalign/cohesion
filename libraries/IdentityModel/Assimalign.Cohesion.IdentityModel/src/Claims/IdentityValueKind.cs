namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Represents the normalized shape of an <see cref="IdentityClaimValue" />.
/// </summary>
/// <remarks>
/// The member values are stable and additive-only, because consumers switch exhaustively over
/// this enum. Both <see cref="Double" /> and <see cref="Decimal" /> exist deliberately:
/// JSON-sourced numbers (OpenID Connect claims) live in the IEEE-754 double domain, while
/// XML-schema-sourced numbers (SAML <c>xs:decimal</c> attribute values) require exact decimal
/// semantics — neither type can represent the other's full domain. Normalizers map integral
/// wire numbers to <see cref="Integer" />, fractional JSON numbers to <see cref="Double" />,
/// and exact decimal wire values to <see cref="Decimal" />; <see cref="DateTime" /> is
/// reserved for source-typed date values (for example SAML <c>xs:dateTime</c>), not for
/// numeric epoch timestamps.
/// </remarks>
public enum IdentityValueKind
{
    /// <summary>
    /// The value shape is not known. This is the shape of an uninitialized
    /// <see cref="IdentityClaimValue" /> and is not a legal claim value.
    /// </summary>
    Undefined = 0,

    /// <summary>
    /// The value is explicitly null.
    /// </summary>
    Null = 1,

    /// <summary>
    /// The value is a string.
    /// </summary>
    String = 2,

    /// <summary>
    /// The value is a Boolean.
    /// </summary>
    Boolean = 3,

    /// <summary>
    /// The value is a 64-bit signed integer.
    /// </summary>
    Integer = 4,

    /// <summary>
    /// The value is an IEEE-754 double-precision number.
    /// </summary>
    Double = 5,

    /// <summary>
    /// The value is an exact decimal number.
    /// </summary>
    Decimal = 6,

    /// <summary>
    /// The value is a date-time with offset.
    /// </summary>
    DateTime = 7,

    /// <summary>
    /// The value is binary data.
    /// </summary>
    Binary = 8,

    /// <summary>
    /// The value is an ordered collection of values.
    /// </summary>
    Array = 9,

    /// <summary>
    /// The value is a set of named members.
    /// </summary>
    Object = 10
}
