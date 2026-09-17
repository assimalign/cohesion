using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
builder.AddAllResources();
builder.UseGateway(args);

await builder.Build().RunAsync();
