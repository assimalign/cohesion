using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
builder.AddTypedIdentity();
builder.UseGateway(args);
await builder.Build().RunAsync();
