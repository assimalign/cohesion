namespace Assimalign.Cohesion.Security;

/// <summary>
/// Creates <see cref="ICertificateManager"/> instances.
/// </summary>
public static class CertificateManagerFactory
{
    /// <summary>
    /// Creates the default platform-agnostic <see cref="ICertificateManager"/>.
    /// </summary>
    /// <returns>A new certificate manager.</returns>
    public static ICertificateManager Create()
    {
        return new CertificateManager();
    }
}
