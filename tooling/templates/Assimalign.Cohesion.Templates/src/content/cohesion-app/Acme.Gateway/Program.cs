using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
// Each referenced resource project contributes a generated verb; the gateway names what it composes.
builder.AddAcmeDatabase();
builder.AddAcmeApi();
builder.UseGateway(args);

await builder.Build().RunAsync();
