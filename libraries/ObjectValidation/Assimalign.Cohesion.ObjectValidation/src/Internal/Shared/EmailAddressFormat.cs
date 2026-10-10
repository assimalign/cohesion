using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Assimalign.Cohesion.ObjectValidation.Internal;

/// <summary>
/// The address format the <c>EmailAddress</c> rule checks, in time linear in the input's length.
/// </summary>
/// <remarks>
/// Web.Validation runs the rule on request bodies, so the check must not cost a client more than the bytes it
/// sends. The backtracking engine took quadratic time on this pattern: <c>a@a.</c> followed by 16,000 letters
/// and a <c>!</c> cost 27.9 s of CPU (#1377).
/// </remarks>
internal static class EmailAddressFormat
{
    /// <summary>
    /// The longest local part, in UTF-8 octets (RFC 5321 §4.5.3.1.1).
    /// </summary>
    internal const int MaxLocalPartOctets = 64;

    /// <summary>
    /// The longest address, in UTF-8 octets: the 256-octet path less its angle brackets (RFC 5321 §4.5.3.1.3).
    /// It also keeps the domain under its own 255-octet limit (§4.5.3.1.2).
    /// </summary>
    internal const int MaxAddressOctets = 254;

    private const string Pattern = @"^((([a-z]|\d|[!#\$%&'\*\+\-\/=\?\^_`{\|}~]|[ -퟿豈-﷏ﷰ-￯])+(\.([a-z]|\d|[!#\$%&'\*\+\-\/=\?\^_`{\|}~]|[ -퟿豈-﷏ﷰ-￯])+)*)|((\x22)((((\x20|\x09)*(\x0d\x0a))?(\x20|\x09)+)?(([\x01-\x08\x0b\x0c\x0e-\x1f\x7f]|\x21|[\x23-\x5b]|[\x5d-\x7e]|[ -퟿豈-﷏ﷰ-￯])|(\\([\x01-\x09\x0b\x0c\x0d-\x7f]|[ -퟿豈-﷏ﷰ-￯]))))*(((\x20|\x09)*(\x0d\x0a))?(\x20|\x09)+)?(\x22)))@((([a-z]|\d|[ -퟿豈-﷏ﷰ-￯])|(([a-z]|\d|[ -퟿豈-﷏ﷰ-￯])([a-z]|\d|-||_|~|[ -퟿豈-﷏ﷰ-￯])*([a-z]|\d|[ -퟿豈-﷏ﷰ-￯])))\.)+(([a-z]|[ -퟿豈-﷏ﷰ-￯])+|(([a-z]|[ -퟿豈-﷏ﷰ-￯])+([a-z]+|\d|-|\.{0,1}|_|~|[ -퟿豈-﷏ﷰ-￯])?([a-z]|[ -퟿豈-﷏ﷰ-￯])))$";

    /// <summary>
    /// The address syntax, built once and matched by the non-backtracking engine, which runs in time linear in the
    /// input's length. The pattern has no backreference, lookaround or atomic group, so that engine accepts exactly
    /// the addresses the backtracking engine did.
    /// </summary>
    /// <remarks>
    /// The timeout is explicit: a process-wide <c>REGEX_DEFAULT_MATCH_TIMEOUT</c> would otherwise apply, and a match
    /// that timed out would throw, which the rule reports as "not invoked", so the value would pass.
    /// </remarks>
    internal static readonly Regex Syntax = new(Pattern, RegexOptions.NonBacktracking, Regex.InfiniteMatchTimeout);

    /// <summary>
    /// Returns whether <paramref name="address"/> is within the RFC 5321 size limits and matches <see cref="Syntax"/>.
    /// </summary>
    /// <param name="address">The address to check.</param>
    /// <returns><see langword="true"/> when the address is well formed; otherwise <see langword="false"/>.</returns>
    internal static bool IsValid(string address)
    {
        // A UTF-8 octet count is never smaller than the UTF-16 length, so this rejects any longer input without
        // reading it.
        if (address.Length > MaxAddressOctets)
        {
            return false;
        }

        // A domain holds no '@' and a quoted local part may, so the last one separates the two.
        int at = address.LastIndexOf('@');

        if (at < 0
            || Encoding.UTF8.GetByteCount(address) > MaxAddressOctets
            || Encoding.UTF8.GetByteCount(address.AsSpan(0, at)) > MaxLocalPartOctets)
        {
            return false;
        }

        return Syntax.IsMatch(address);
    }
}
