namespace Assimalign.Cohesion.Web.Diagnostics;

/// <summary>
/// The on-disk line format the <see cref="W3CAccessLogProvider"/> writes.
/// </summary>
public enum AccessLogFormat
{
    /// <summary>
    /// The W3C Extended Log File Format: <c>#Version</c>/<c>#Fields</c> directives at the top
    /// of every file, space-separated fields, <c>-</c> for absent values, spaces inside string
    /// fields encoded as <c>+</c>. The field list is fixed:
    /// <c>date time c-ip cs-method cs-uri-stem cs-uri-query sc-status cs-bytes sc-bytes
    /// time-taken cs-version cs-host cs(User-Agent) cs(Referer)</c>.
    /// </summary>
    W3CExtended = 0,

    /// <summary>
    /// The NCSA Common Log Format:
    /// <c>host ident authuser [date] "request" status bytes</c>. Ident and authuser are always
    /// <c>-</c>; timestamps are UTC (<c>+0000</c>).
    /// </summary>
    Common = 1,

    /// <summary>
    /// The NCSA Combined Log Format: <see cref="Common"/> plus quoted <c>Referer</c> and
    /// <c>User-Agent</c> fields.
    /// </summary>
    Combined = 2,
}
