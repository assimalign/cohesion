
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Net.Transports;

public static class HttpHostBuilderExtensions
{
    public static IHostBuilder AddHttpServer(this IHostBuilder builder)
    {
  

        return builder;
    }


    public static IHostBuilder AddHttpServer(this IHostBuilder builder, Action<HttpServerBuilder> configure)
    {
        if (configure is null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        var serverBuilder = new HttpServerBuilder();

        configure.Invoke(serverBuilder);

        return builder.AddServer(serverBuilder);
    }


    public static TcpServerTransportOptions ConfigureHttps(this TcpServerTransportOptions options, X509Certificate2 certificate)
    {
        options.AddMiddleware(builder =>
        {
            builder.UseNext(async (context, next) =>
            {
                var pipe = context.Connection.Pipe;
                var stream = pipe.GetStream();
                var sslStream = new SslStream(stream);

                await sslStream.AuthenticateAsServerAsync(certificate);

                context.SetPipe(new TransportConnectionPipe(sslStream));

                await next.Invoke(context);
            });
        });

        return options;
    }
    public static TcpServerTransportOptions ConfigureHttps(this TcpServerTransportOptions options, Action<SslServerAuthenticationOptions> configure)
    {
        options.AddMiddleware(builder =>
        {
            builder.UseNext(async (context, next) =>
            {
                var pipe = context.Connection.Pipe;
                var stream = pipe.GetStream();
                var sslStream = new SslStream(stream);
                var options = new SslServerAuthenticationOptions();

                configure.Invoke(options);

                await sslStream.AuthenticateAsServerAsync(options);

                context.SetPipe(new TransportConnectionPipe(sslStream));

                await next.Invoke(context);
            });
        });

        return options;
    }
}