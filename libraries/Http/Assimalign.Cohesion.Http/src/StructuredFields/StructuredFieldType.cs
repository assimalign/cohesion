namespace Assimalign.Cohesion.Http;

/// <summary>
/// Identifies the concrete kind of an RFC 9651 bare item held by a
/// <see cref="StructuredFieldBareItem"/>.
/// </summary>
/// <remarks>
/// RFC 9651 &#167; 3.3 defines eight bare item types. RFC 9651 obsoletes RFC 8941 and
/// adds <see cref="Date"/> (&#167; 3.3.7) and <see cref="DisplayString"/> (&#167; 3.3.8) to the
/// original six.
/// </remarks>
public enum StructuredFieldType
{
    /// <summary>
    /// An integer in the range -999,999,999,999,999 to 999,999,999,999,999
    /// (RFC 9651 &#167; 3.3.1).
    /// </summary>
    Integer,

    /// <summary>
    /// A decimal with at most 12 integer digits and 3 fractional digits
    /// (RFC 9651 &#167; 3.3.2).
    /// </summary>
    Decimal,

    /// <summary>
    /// A string of printable ASCII characters (RFC 9651 &#167; 3.3.3).
    /// </summary>
    String,

    /// <summary>
    /// A token: a short textual word beginning with an alpha or <c>*</c>
    /// (RFC 9651 &#167; 3.3.4).
    /// </summary>
    Token,

    /// <summary>
    /// A sequence of bytes conveyed as base64 (RFC 9651 &#167; 3.3.5).
    /// </summary>
    ByteSequence,

    /// <summary>
    /// A boolean (RFC 9651 &#167; 3.3.6).
    /// </summary>
    Boolean,

    /// <summary>
    /// A date: seconds relative to the Unix epoch (RFC 9651 &#167; 3.3.7).
    /// </summary>
    Date,

    /// <summary>
    /// A Unicode string conveyed as percent-encoded UTF-8 (RFC 9651 &#167; 3.3.8).
    /// </summary>
    DisplayString,
}
