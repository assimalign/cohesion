namespace Assimalign.Cohesion.Web.Authentication.Bearer;

/// <summary>
/// Well-known defaults for the JWT bearer authentication handler.
/// </summary>
public static class JwtBearerDefaults
{
    /// <summary>
    /// The default scheme name (<c>"Bearer"</c>).
    /// </summary>
    public const string AuthenticationScheme = "Bearer";

    /// <summary>
    /// The <c>Authorization</c> header credential scheme token (<c>"Bearer"</c>, RFC 6750 §2.1).
    /// </summary>
    public const string BearerPrefix = "Bearer";
}
