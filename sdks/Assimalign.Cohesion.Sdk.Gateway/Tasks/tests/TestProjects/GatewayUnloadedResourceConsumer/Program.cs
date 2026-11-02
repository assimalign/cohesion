using Assimalign.Cohesion.ApplicationModel.Gateway;
using Assimalign.Cohesion.ApplicationModel.Gateway.InProcess;

// The build reports the reference to a resource the gateway does not load; the program never runs.
ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("smoke").UseInProcess();
builder.AddResource("Web", "smoke-api");
await using ApplicationGateway gateway = builder.Build();
