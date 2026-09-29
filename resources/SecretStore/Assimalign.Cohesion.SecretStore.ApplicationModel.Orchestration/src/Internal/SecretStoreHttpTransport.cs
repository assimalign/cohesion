using System.Net.Http;
using System.Net.Security;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

/// <summary>
/// Creates the per-operation HTTP transport the SecretStore providers hand the SecretStore client:
/// redirects and cookies disabled, and the store's TLS certificate validated by the
/// gateway-supplied validator when one is present.
/// </summary>
/// <remarks>
/// The settings match the gateway transport the store reads used before the provider seams, so
/// every read and command presents the same HTTP behaviour to the store.
/// </remarks>
internal static class SecretStoreHttpTransport
{
    /// <summary>
    /// Creates a transport that owns and disposes its handler.
    /// </summary>
    /// <param name="serverCertificateValidator">
    /// The validator for the store's TLS certificate, or <see langword="null"/> for the platform
    /// default validation.
    /// </param>
    /// <returns>A transport the caller disposes once its requests finish.</returns>
    internal static HttpMessageInvoker Create(RemoteCertificateValidationCallback? serverCertificateValidator) =>
        new(CreateHandler(serverCertificateValidator), disposeHandler: true);

    /// <summary>
    /// Creates the socket handler behind <see cref="Create"/>.
    /// </summary>
    /// <param name="serverCertificateValidator">
    /// The validator for the store's TLS certificate, or <see langword="null"/> for the platform
    /// default validation.
    /// </param>
    /// <returns>The configured handler.</returns>
    internal static SocketsHttpHandler CreateHandler(RemoteCertificateValidationCallback? serverCertificateValidator)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
        };

        if (serverCertificateValidator is not null)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = serverCertificateValidator;
        }

        return handler;
    }
}
