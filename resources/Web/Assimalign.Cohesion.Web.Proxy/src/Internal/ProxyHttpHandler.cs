using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Proxy.Internal;

/// <summary>
/// Creates the connection pool a proxy setting owns: a <see cref="SocketsHttpHandler"/> configured for
/// proxying, adjusted by the application's <c>ConfigureHandler</c> callback.
/// </summary>
internal static class ProxyHttpHandler
{
    /// <summary>
    /// How long the handler waits for a connection to a destination before the request is answered
    /// with 504 Gateway Timeout, unless <c>ConfigureHandler</c> changes it.
    /// </summary>
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Creates an invoker over a new handler, which the invoker disposes.
    /// </summary>
    /// <param name="configure">The application's callback, or <see langword="null"/>.</param>
    /// <param name="forwardProxy">
    /// <see langword="true"/> for a forward proxy, which connects to each destination itself and so
    /// refuses a <c>ConnectCallback</c> or a proxy of the application's.
    /// </param>
    /// <param name="connectCallback">The connect step to install after the callback ran, or <see langword="null"/>.</param>
    /// <exception cref="InvalidOperationException">The callback left the handler unable to proxy.</exception>
    public static HttpMessageInvoker CreateInvoker(
        Action<SocketsHttpHandler>? configure,
        bool forwardProxy,
        Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>? connectCallback)
    {
        // A proxy relays what the destination sends: it follows no redirect, keeps no cookie jar and
        // decodes no body. It sends no traceparent of its own, so the client's trace continues through it.
        SocketsHttpHandler handler = new()
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ActivityHeadersPropagator = null,
            ConnectTimeout = DefaultConnectTimeout,
            EnableMultipleHttp2Connections = true,
        };

        HttpMessageInvoker? invoker = null;
        try
        {
            configure?.Invoke(handler);

            string? unsafeProperty = handler.AllowAutoRedirect ? nameof(SocketsHttpHandler.AllowAutoRedirect)
                : handler.UseCookies ? nameof(SocketsHttpHandler.UseCookies)
                : handler.AutomaticDecompression != DecompressionMethods.None ? nameof(SocketsHttpHandler.AutomaticDecompression)
                : null;

            if (unsafeProperty is not null)
            {
                throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.HandlerCannotProxy, unsafeProperty));
            }

            if (forwardProxy)
            {
                string? connectProperty = handler.ConnectCallback is not null ? nameof(SocketsHttpHandler.ConnectCallback)
                    : handler.UseProxy ? nameof(SocketsHttpHandler.UseProxy)
                    : null;

                if (connectProperty is not null)
                {
                    throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.ForwardProxyHandlerConnects, connectProperty));
                }
            }

            if (connectCallback is not null)
            {
                handler.ConnectCallback = connectCallback;
            }

            invoker = new HttpMessageInvoker(handler, disposeHandler: true);
            return invoker;
        }
        finally
        {
            if (invoker is null)
            {
                handler.Dispose();
            }
        }
    }
}
