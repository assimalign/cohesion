namespace Assimalign.Cohesion.Web.Results.Internal;

/// <summary>
/// Default media types shared by the built-in results.
/// </summary>
internal static class HttpResultDefaults
{
    /// <summary>The default media type for <c>Text</c>/<c>Content</c> results.</summary>
    public const string TextMediaType = "text/plain; charset=utf-8";

    /// <summary>The default media type for <c>Json</c>/<c>Ok</c> results.</summary>
    public const string JsonMediaType = "application/json; charset=utf-8";
}
