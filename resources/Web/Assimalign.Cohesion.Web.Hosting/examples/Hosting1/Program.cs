using Assimalign.Cohesion.Http.Transports;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Hosting;
using Assimalign.Cohesion.Configuration;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using Assimalign.Cohesion.Configuration.Json;
using Assimalign.Cohesion.Web.Routing;
using System.Threading.Tasks;

using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
CancellationToken cancellationToken = cancellationTokenSource.Token;

WebApplicationBuilder builder = WebApplication.CreateBuilder();

AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);


builder.ServerManager.ConfigureServer(options =>
{
    options.UseHttp1(transport =>
    {
        transport.EndPoint = new IPEndPoint(IPAddress.Loopback, 8085);
        transport.Use((connection, context, next, token) =>
        {
            Console.WriteLine($"Listening On: {context.LocalEndPoint}");

            return next.Invoke(connection, context, token);
        });
    });
    options.UseHttp2(transport =>
    {
        transport.EndPoint = new IPEndPoint(IPAddress.Loopback, 8082);
    });
});

builder.AddRouting();



WebApplication app = builder.Build();

app.UseRouting();


app.MapGet("/test", context =>
{


    return Task.CompletedTask;
});

app.Use((context, next) =>
{
    Console.WriteLine("Received request: " + context.Request.Path);

    return next.Invoke(context);

});

await app.RunAsync();