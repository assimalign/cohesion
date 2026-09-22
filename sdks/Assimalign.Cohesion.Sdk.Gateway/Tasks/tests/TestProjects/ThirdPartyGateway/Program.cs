var builder = Gateway.CreateBuilder(args);
builder.AddThirdPartyResource();
builder.UseGateway(args);
await builder.Build().RunAsync();
