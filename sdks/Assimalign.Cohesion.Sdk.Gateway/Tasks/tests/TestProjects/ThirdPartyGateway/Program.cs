using Assimalign.Cohesion.ApplicationModel;

var builder = Gateway.CreateBuilder(args);
// The third-party ApplicationModel package's own verb over the generated manifest.
builder.AddThirdParty(Manifests.ThirdPartyResource);
builder.UseGateway(args);
await builder.Build().RunAsync();
