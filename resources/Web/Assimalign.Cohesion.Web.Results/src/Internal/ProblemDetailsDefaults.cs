namespace Assimalign.Cohesion.Web.Results.Internal;

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
            // HttpStatusCode.ToString() renders "<code> <phrase>" (e.g. "404 Not Found"); take the
            // phrase after the first space so the title carries only the human-readable portion.
            string text = new HttpStatusCode(status).ToString();
            int space = text.IndexOf(' ');
            if (space >= 0 && space + 1 < text.Length)
            {
                return text[(space + 1)..];
            }
        }

        return status >= 500 ? "Internal Server Error" : "Error";
    }
}
