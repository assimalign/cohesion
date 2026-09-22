using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
builder.AddTypedLogs();
builder.UseGateway(args);
await builder.Build().RunAsync();
