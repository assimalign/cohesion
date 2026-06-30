using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Hosting;
using Assimalign.Cohesion.Configuration;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Http;
using System.Linq;

using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

CancellationToken cancellationToken = cancellationTokenSource.Token;

WebApplicationBuilder builder = WebApplication.CreateBuilder();

AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

builder.Server
    .UseServer((serviceProvider, options) =>
    {
        options.UseHttp1(tcp =>
        {

        });

        //options.UseHttp1(transport =>
        //{
        //    transport.EndPoint = new IPEndPoint(IPAddress.Loopback, 8085);
        //    transport.Use((connection, context, next, token) =>
        //    {
        //        return next.Invoke(connection, context, token);
        //    });

        //    Console.WriteLine($"Listening On: {transport.EndPoint}");
        //});
        //options.UseHttp2(transport =>
        //{
        //    transport.EndPoint = new IPEndPoint(IPAddress.Loopback, 8082);
        //});
    });

WebApplication app = builder.Build();


//app.UseRouting();
//app.MapGet("/test", (context) =>
//{
//    Console.WriteLine("Received request: " + context.Request.Path);


//    return Task.CompletedTask;
//});

await app.RunAsync();