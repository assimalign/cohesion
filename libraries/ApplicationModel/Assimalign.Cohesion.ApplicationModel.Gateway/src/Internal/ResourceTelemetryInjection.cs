using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

internal sealed record ResourceTelemetryInjection(Uri Endpoint, ReadOnlyMemory<byte> HeadersDocument)
{
    internal static void Apply(ResourceTelemetryInjection? telemetry, IDictionary<string, string> environment)
    {
        if (telemetry is null)
        {
            GatewayEnvironmentVariables.Remove(environment, ResourceEnvironment.TelemetryEndpoint);
            GatewayEnvironmentVariables.Remove(environment, ResourceEnvironment.TelemetryProtocol);
            GatewayEnvironmentVariables.Remove(environment, ResourceEnvironment.TelemetryHeadersPath);
            return;
        }
        GatewayEnvironmentVariables.Set(environment, ResourceEnvironment.TelemetryEndpoint, telemetry.Endpoint.ToEndpointString());
        GatewayEnvironmentVariables.Set(environment, ResourceEnvironment.TelemetryProtocol, "otlp-http");
        GatewayEnvironmentVariables.Remove(environment, ResourceEnvironment.TelemetryHeadersPath);
    }
}
