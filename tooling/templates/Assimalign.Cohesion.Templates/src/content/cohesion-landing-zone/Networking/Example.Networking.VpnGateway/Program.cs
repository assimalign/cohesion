using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.VpnGateway;
using Assimalign.Cohesion.VpnGateway.Hosting;

// Owns the WireGuard listener, peer set, routes, and key-backed transport policy.
IVpnGatewayApplicationBuilder builder = VpnGatewayApplication.CreateBuilder(args);

await builder.Build().RunAsync();
