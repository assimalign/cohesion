namespace Assimalign.Cohesion.Web.SecurityHeaders;

/// <summary>
/// The <c>Cross-Origin-Embedder-Policy</c> response field values (HTML Standard, "Cross-origin embedder
/// policies"): which cross-origin resources a document may load.
/// </summary>
/// <remarks>
/// Opt-in: the default security-headers policy emits no field. <see cref="RequireCorp"/> blocks every
/// cross-origin subresource that does not opt in through <c>Cross-Origin-Resource-Policy</c> or CORS, so
/// enable it only once every embedded resource does.
/// </remarks>
public enum CrossOriginEmbedderPolicy
{
    /// <summary><c>unsafe-none</c>: load cross-origin resources without an explicit opt-in.</summary>
    UnsafeNone,

    /// <summary>
    /// <c>require-corp</c>: load a cross-origin resource only when it opts in through
    /// <c>Cross-Origin-Resource-Policy</c> or CORS.
    /// </summary>
    RequireCorp,

    /// <summary>
    /// <c>credentialless</c>: load no-CORS cross-origin resources without credentials instead of
    /// requiring an explicit opt-in.
    /// </summary>
    Credentialless,
}
