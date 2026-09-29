namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>Describes the outcome of verifying one presented resource credential.</summary>
public enum ResourceCredentialStatus
{
    /// <summary>
    /// The verifier does not recognize the credential; the resource falls through to its default
    /// application-key verification.
    /// </summary>
    NoResult,

    /// <summary>The credential is missing, malformed, untrusted, or expired (HTTP 401).</summary>
    Unauthorized,

    /// <summary>The credential is authentic but not valid for this resource or operation (HTTP 403).</summary>
    Forbidden,

    /// <summary>The credential is authentic and issued for this resource.</summary>
    Authorized,
}
