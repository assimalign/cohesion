using Assimalign.Cohesion.VpnGateway;
using Assimalign.Cohesion.VpnGateway.Hosting;

// Owns the WireGuard listener, peer set, routes, and key-backed transport policy.
VpnGatewayApplicationBuilder builder = VpnGatewayApplication.CreateBuilder(args);

await using VpnGatewayApplication application = builder.Build();
await application.RunAsync();
