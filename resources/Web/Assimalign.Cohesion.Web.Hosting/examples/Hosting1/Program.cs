using Assimalign.Cohesion.Http.Transports;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Hosting;
using Assimalign.Cohesion.Configuration;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using Assimalign.Cohesion.Web.Routing;
using System.Threading.Tasks;
using Assimalign.Cohesion.DependencyInjection;

using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

CancellationToken cancellationToken = cancellationTokenSource.Token;

WebApplicationBuilder builder = WebApplication.CreateBuilder();

AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

builder.Server
    .UseServer(options =>
    {
        options.UseHttp1(transport =>
        {
            transport.EndPoint = new IPEndPoint(IPAddress.Loopback, 8085);
            transport.Use((connection, context, next, token) =>
            {
                return next.Invoke(connection, context, token);
            });

            Console.WriteLine($"Listening On: {transport.EndPoint}");
        });
        options.UseHttp2(transport =>
        {
            transport.EndPoint = new IPEndPoint(IPAddress.Loopback, 8082);
        });
    });

builder.AddRouting();

WebApplication app = builder.Build();


app.UseRouting();
app.MapGet("/test", (context) =>
{
    Console.WriteLine("Received request: " + context.Request.Path);


    return Task.CompletedTask;
});

await app.RunAsync();