using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Hosting.Management;

/// <summary>Resolves authorized immutable certificate versions through an explicitly configured provider.</summary>
public interface IResourceCertificateResolver
{
    /// <summary>Loads a certificate with its private key; ownership transfers to the hosting adapter.</summary>
    /// <param name="reference">The exact provider, key and immutable version to resolve.</param>
    /// <param name="cancellationToken">Signals cancellation.</param>
    /// <returns>The owned certificate for that exact version.</returns>
    ValueTask<X509Certificate2> ResolveAsync(ResourceCertificateReference reference, CancellationToken cancellationToken = default);
}
