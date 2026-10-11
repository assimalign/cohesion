namespace Assimalign.Cohesion.Web.Internal;

using Assimalign.Cohesion.Http;

/// <summary>
/// Shared constants and helpers for the ProblemDetails family: the problem+json media type, the
/// reserved default type, the well-known diagnostics-suppression item key, and status-phrase title
/// resolution.
/// </summary>
internal static class ProblemDetailsDefaults
{
    /// <summary>The RFC 9457 media type for a problem details payload.</summary>
    public const string MediaType = "application/problem+json";

    /// <summary>The reserved default problem type when none is supplied (RFC 9457 §4.2).</summary>
    public const string DefaultType = "about:blank";

    /// <summary>
    /// Resolves the human-readable title for an HTTP status code &#8212; its reason phrase for known
    /// codes, or a class-appropriate generic title otherwise.
    /// </summary>
    /// <param name="status">The numeric HTTP status code.</param>
    /// <returns>The title to use for the <c>"about:blank"</c> problem type.</returns>
    public static string GetTitle(int status)
    {
        if (HttpStatusCode.IsValid(status))
        {
            // The registered reason phrase (RFC 9110 §15), e.g. "Not Found" or "Content Too Large".
            return new HttpStatusCode(status).ReasonPhrase;
        }

        return status >= 500 ? "Internal Server Error" : "Error";
    }
}
