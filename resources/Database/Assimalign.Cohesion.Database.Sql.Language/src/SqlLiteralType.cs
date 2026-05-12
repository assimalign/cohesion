namespace Assimalign.Cohesion.Database.Language.Sql;

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
}
