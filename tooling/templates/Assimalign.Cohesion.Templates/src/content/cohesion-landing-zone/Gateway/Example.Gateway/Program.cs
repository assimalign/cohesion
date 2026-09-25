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

IApplicationSet set = Application.CreateSet(new LocalGateway(gatewayOptions), applicationSetArgs)
    .AddApplication(Applications.Platform)
    .AddApplication(Applications.Identity)
    .AddApplication(Applications.Networking)
    .AddApplication(Applications.AppA)
    .AddApplication(Applications.AppB)
    .AddApplication(Applications.AppC);

await set.RunAsync();
