using Assimalign.Cohesion.IdentityModel;

namespace Assimalign.Cohesion.IdentityHub.Models;

/// <summary>
/// Associates an IdentityHub application record with canonical credential metadata.
/// </summary>
public class ApplicationCredential
{
    /// <summary>
    /// Gets or sets the application that owns the credential.
    /// </summary>
    public ApplicationId ApplicationId { get; set; }

    /// <summary>
    /// Gets or sets the canonical credential metadata. Secret material is never stored here.
    /// </summary>
    public IdentityCredential? Credential { get; set; }
}
