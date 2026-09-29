using System;

using Assimalign.Cohesion;
using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Gateway;

// The generated Applications members resolve each area model by invoking that gateway's
// describe mode in Local, then one Local gateway owns the combined lifecycle.
bool hasEnvironmentArgument = Array.Exists(
    args,
    argument => string.Equals(argument, "--environment", StringComparison.OrdinalIgnoreCase)
        || argument.StartsWith("--environment=", StringComparison.OrdinalIgnoreCase));
bool hasEnvironmentVariable =
    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppEnvironment.Keys.EnvironmentKey))
    || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppEnvironment.Keys.DotNetEnvironmentKey));
string[] applicationSetArgs = hasEnvironmentArgument || hasEnvironmentVariable
    ? args
    : [.. args, "--environment", AppEnvironment.Keys.Local];

var gatewayOptions = new LocalGatewayOptions();
ApplicationGatewayCommandLine.Apply(gatewayOptions, applicationSetArgs);

// A member's model arrives from its gateway's describe output and carries no providers, so the set
// registers them for the member that needs them, as that member's own Program.cs does: Platform's
// stores, certificate authority, trust store and telemetry sink. Identity and Networking read their
// secrets from gateway parameters, and the zones read no store.
IApplicationSet set = Application.CreateSet(new LocalGateway(gatewayOptions), applicationSetArgs)
    .AddApplication(Applications.Platform, platform =>
    {
        platform.UseSecretStore("platform-secretstore").AsCertificateAuthority().AsTrustStore();
        platform.UseConfigurationStore("platform-configurationstore");
        platform.Providers.Telemetry = ResourceTelemetrySink.FromResource("platform-logspace");
    })
    .AddApplication(Applications.Identity)
    .AddApplication(Applications.Networking)
    .AddApplication(Applications.AppA)
    .AddApplication(Applications.AppB)
    .AddApplication(Applications.AppC);

await set.RunAsync();
