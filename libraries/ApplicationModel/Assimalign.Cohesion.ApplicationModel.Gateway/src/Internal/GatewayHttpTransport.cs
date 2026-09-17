using System.Net.Http;
using System.Net.Security;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

internal static class GatewayHttpTransport
{
    internal static HttpMessageInvoker Create(RemoteCertificateValidationCallback? serverCertificateValidator)
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false };
        if (serverCertificateValidator is not null)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = serverCertificateValidator;
        }
        return new HttpMessageInvoker(handler, disposeHandler: true);
    }
}
