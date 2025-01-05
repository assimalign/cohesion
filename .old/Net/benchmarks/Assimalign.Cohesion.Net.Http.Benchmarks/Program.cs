using Assimalign.Cohesion.Net.Http;

var builder = new HttpServerBuilder();

builder.ConfigureServer(options =>
{
    options.UseTcpTransport(options =>
    {
        options.AddMiddleware(middleware =>
        {
            middleware.UseNext((context, next) =>
            {
                return next(context);
            });
        });
    });
});

var server = builder.Build();