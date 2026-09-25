using System.Net.Security;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>Supplies the outbound TLS trust a dispatcher uses to reach one application's realized resources.</summary>
public interface IResourceTransportTrustProvider
{
    /// <summary>Creates a validator over the application's transport anchors, or null when it has none.</summary>
    /// <param name="application">The application that realizes the target resource.</param>
    /// <returns>The application's outbound TLS validator, or <see langword="null"/> when it has no anchors.</returns>
    RemoteCertificateValidationCallback? CreateOutboundTrustValidator(ApplicationName application);
}
