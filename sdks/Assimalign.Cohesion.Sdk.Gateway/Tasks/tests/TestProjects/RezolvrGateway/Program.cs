using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
builder.AddTypedRezolvr();
builder.UseGateway(args);
await builder.Build().RunAsync();
