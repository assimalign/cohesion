namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>
/// Classifies the type of a SQL literal value.
/// </summary>
public enum SqlLiteralType
{
    /// <summary>
    /// A string literal (e.g., <c>'hello'</c>).
    /// </summary>
    String,

    /// <summary>
    /// An integer literal (e.g., <c>42</c>).
    /// </summary>
    Integer,

    /// <summary>
    /// A floating-point literal (e.g., <c>3.14</c>).
    /// </summary>
    Float,

    /// <summary>
    /// The <c>NULL</c> literal.
    /// </summary>
    Null,

    /// <summary>
    /// A boolean literal (<c>TRUE</c> or <c>FALSE</c>).
    /// </summary>
    Boolean,

    /// <summary>A DATE literal with canonical yyyy-MM-dd text.</summary>
    Date,

    /// <summary>A zone-less TIME literal with canonical HH:mm:ss[.fffffff] text.</summary>
    Time,

    /// <summary>A zone-less TIMESTAMP literal with canonical date and time text.</summary>
    Timestamp,

    /// <summary>A TIMESTAMPTZ literal with canonical date, time and offset text.</summary>
    TimestampWithTimeZone,

    /// <summary>A day-time INTERVAL literal with canonical ISO 8601 duration text.</summary>
    Interval,

    /// <summary>An X'...' binary literal with uppercase hexadecimal text.</summary>
    Binary,
}
