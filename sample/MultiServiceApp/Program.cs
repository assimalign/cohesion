using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Net.Http;
using Assimalign.Cohesion.Net.Transports;

var builder = HostBuilder.Create();

builder.AddConfigurationStore();
builder.AddLogSpace();
builder.AddEventHub();
builder.AddMessageHub();
builder.AddHttpServer(server =>
{
    server.ConfigureServer(options =>
    {
        options.UseTcpTransport(options =>
        {
            options.AddMiddleware(builder =>
            {
                builder.UseNext(async (c, n) =>
                {
                    await n(c);
                });
            });
            options.AddTraceHandler((code, data, message) =>
            {

            });
        });
    });
});



var host = builder.Build();


host.Run();