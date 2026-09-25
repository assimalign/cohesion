using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// Configures one code-first OAuth client registration.
/// </summary>
/// <remarks>
/// A non-empty <see cref="ClientSecret"/> enables the client-credentials grant.
/// <see cref="AllowDeviceAuthorization"/> independently enables the OAuth device grant.
/// Every value in <see cref="Audiences"/> must also be declared through
/// <see cref="IIdentityHubApplicationBuilder.AddAudience(string)"/>.
/// </remarks>
public sealed class IdentityHubClientOptions
{
    /// <summary>
    /// Gets or sets the confidential-client secret. Leave null to disable client credentials.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Gets or sets whether this client may initiate device authorization.
    /// </summary>
    public bool AllowDeviceAuthorization { get; set; }

    /// <summary>
    /// Gets or sets the lifetime of tokens issued to this client.
    /// </summary>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Gets the audience identifiers this client may request.
    /// </summary>
    public IList<string> Audiences { get; } = new List<string>();
}
