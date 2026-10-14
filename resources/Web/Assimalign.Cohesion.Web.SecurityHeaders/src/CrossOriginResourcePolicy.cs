namespace Assimalign.Cohesion.Web.SecurityHeaders;

/// <summary>
/// The <c>Cross-Origin-Resource-Policy</c> response field values (Fetch Standard, "Cross-Origin-Resource-Policy
/// header"): which origins may load this response as a no-CORS subresource.
/// </summary>
/// <remarks>
/// Opt-in: the default security-headers policy emits no field. It defends against speculative-execution
/// side channels that read cross-origin responses, and it is the opt-in a cross-origin isolated embedder
/// (<see cref="CrossOriginEmbedderPolicy.RequireCorp"/>) looks for.
/// </remarks>
public enum CrossOriginResourcePolicy
{
    /// <summary><c>same-site</c>: only documents of the same site may load the response.</summary>
    SameSite,

    /// <summary><c>same-origin</c>: only documents of the same origin may load the response.</summary>
    SameOrigin,

    /// <summary><c>cross-origin</c>: any document may load the response.</summary>
    CrossOrigin,
}
