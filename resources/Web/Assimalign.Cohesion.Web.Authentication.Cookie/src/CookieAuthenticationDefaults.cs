namespace Assimalign.Cohesion.Web.Authentication.Cookie;

/// <summary>
/// Well-known defaults for the cookie authentication handler.
/// </summary>
public static class CookieAuthenticationDefaults
{
    /// <summary>
    /// The default scheme name (<c>"Cookies"</c>).
    /// </summary>
    public const string AuthenticationScheme = "Cookies";

    /// <summary>
    /// The prefix applied to the default cookie name.
    /// </summary>
    public const string CookiePrefix = ".Cohesion.";

    /// <summary>
    /// The default login path a browser challenge redirects to.
    /// </summary>
    public const string LoginPath = "/Account/Login";

    /// <summary>
    /// The default logout path.
    /// </summary>
    public const string LogoutPath = "/Account/Logout";

    /// <summary>
    /// The default access-denied path a browser forbid redirects to.
    /// </summary>
    public const string AccessDeniedPath = "/Account/AccessDenied";

    /// <summary>
    /// The default query-string parameter carrying the post-login return URL.
    /// </summary>
    public const string ReturnUrlParameter = "ReturnUrl";
}
