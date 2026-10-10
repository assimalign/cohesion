namespace Assimalign.Cohesion.Web.Rewrite;

/// <summary>
/// The part of the request URL a pattern rule's regular expression is matched against.
/// </summary>
/// <remarks>
/// <para>
/// The path is matched as routing sees it: percent-decoded, always starting with <c>/</c>, and inside a
/// <c>Map(path)</c> branch the path below the branch's prefix. The query is matched in its re-encoded form:
/// each key and value percent-encoded (<see cref="System.Uri.EscapeDataString(string)"/>), joined with
/// <c>&amp;</c>, in the order the parsed query collection enumerates them, and a key with an empty value
/// written without <c>=</c>. The request's raw query string is not available to the pipeline, which is why
/// the query is rebuilt.
/// </para>
/// </remarks>
public enum RewriteMatchTarget
{
    /// <summary>
    /// The path alone, for example <c>/products/7</c>. The default.
    /// </summary>
    Path = 0,

    /// <summary>
    /// The path, then <c>?</c> and the re-encoded query when the request has one, for example
    /// <c>/item.php?id=7</c>. A request without a query is matched as its path alone.
    /// </summary>
    PathAndQuery = 1,
}
