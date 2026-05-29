using System;
using System.Collections.Generic;
using System.Net.Security;
using System.Text;

namespace Assimalign.Cohesion.Transports;

public static class TcpTransportExtensions
{

    extension(TcpServerTransportOptions options)
    {
        public TcpServerTransportOptions UseSecureConnection(Action<SslServerAuthenticationOptions> configure)
        {
            Action<SslServerAuthenticationOptions> configure1 = 
                ArgumentNullException.ThrowIfNull<Action<SslServerAuthenticationOptions>>(configure);

            return options.Use(async (context, next) =>
            {
                // Get the connection pipe
                ITransportConnectionPipe pipe = context.Pipe;

                // Configure the SSL options and generate the SSL stream
                SslServerAuthenticationOptions options = new();
                configure1.Invoke(options);
                SslStream stream = new SslStream(pipe.GetStream(), false);

                // Authenticate the SSL stream as a server
                await stream.AuthenticateAsServerAsync(options, context.ConnectionCancelled);

                // Overwrite the connection pipe with the SSL stream
                context.SetPipe(new TransportConnectionPipe(stream));

                await next.Invoke(context).ConfigureAwait(false);
            });
        }
    }



    extension(TcpClientTransportOptions options)
    {
        public TcpClientTransportOptions UseSecureConnection(Action<SslClientAuthenticationOptions> configure)
        {
            Action<SslClientAuthenticationOptions> configure1 =
                ArgumentNullException.ThrowIfNull<Action<SslClientAuthenticationOptions>>(configure);

            return options.Use(async (context, next) =>
            {
                // Get the connection pipe
                ITransportConnectionPipe pipe = context.Pipe;

                // Configure the SSL options and generate the SSL stream
                SslClientAuthenticationOptions options = new();
                configure1.Invoke(options);
                SslStream stream = new SslStream(
                    pipe.GetStream(), 
                    leaveInnerStreamOpen: false,
                    userCertificateValidationCallback: static (_, _, _, _) => true);

                // Authenticate the SSL stream as a client
                await stream.AuthenticateAsClientAsync(options, context.ConnectionCancelled);

                // Overwrite the connection pipe with the SSL stream
                context.SetPipe(new TransportConnectionPipe(stream));

                await next.Invoke(context).ConfigureAwait(false);
            });
        }
    }
}
