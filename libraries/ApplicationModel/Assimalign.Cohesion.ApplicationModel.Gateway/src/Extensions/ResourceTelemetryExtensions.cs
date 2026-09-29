using System;
using System.Collections.Generic;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>Exposes gateway-prepared telemetry to platform resource controllers.</summary>
public static class ResourceTelemetryExtensions
{
    /// <summary>Reads telemetry prepared for a resource reconciliation.</summary>
    /// <param name="context">The controller context supplied by the gateway.</param>
    extension(IResourceControlContext context)
    {
        /// <summary>Gets the telemetry configuration prepared by the gateway for this reconciliation.</summary>
        /// <returns>The prepared telemetry, or null when none is available or the context is externally supplied.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
        public IResourceTelemetry? GetTelemetry()
        {
            ArgumentNullException.ThrowIfNull(context);
            // Capture gateway-owned state without adding a capability to the public control-context contract.
            return (context as ResourceControlContext)?.Telemetry;
        }
    }

    /// <summary>Applies optional telemetry to a resource environment.</summary>
    /// <param name="telemetry">The prepared telemetry, or null to remove existing telemetry values.</param>
    extension(IResourceTelemetry? telemetry)
    {
        /// <summary>Applies the telemetry endpoint and protocol, removing stale values when telemetry is absent.</summary>
        /// <param name="environment">The mutable resource environment; its headers path is cleared until materialization.</param>
        /// <exception cref="ArgumentNullException"><paramref name="environment"/> is null.</exception>
        /// <exception cref="InvalidOperationException">The telemetry endpoint is not a valid absolute endpoint URI.</exception>
        public void ApplyEnvironment(IDictionary<string, string> environment)
        {
            ArgumentNullException.ThrowIfNull(environment);
            if (telemetry is null)
            {
                GatewayEnvironmentVariables.Remove(environment, AppEnvironment.Variables.TelemetryEndpoint);
                GatewayEnvironmentVariables.Remove(environment, AppEnvironment.Variables.TelemetryProtocol);
                GatewayEnvironmentVariables.Remove(environment, AppEnvironment.Variables.TelemetryHeadersPath);
                return;
            }

            GatewayEnvironmentVariables.Set(environment, AppEnvironment.Variables.TelemetryEndpoint, telemetry.Endpoint.ToEndpointString());
            GatewayEnvironmentVariables.Set(environment, AppEnvironment.Variables.TelemetryProtocol, "otlp-http");
            GatewayEnvironmentVariables.Remove(environment, AppEnvironment.Variables.TelemetryHeadersPath);
        }
    }
}
