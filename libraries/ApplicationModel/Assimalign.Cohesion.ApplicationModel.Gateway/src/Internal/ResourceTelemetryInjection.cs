using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

internal sealed record ResourceTelemetryInjection(Uri Endpoint, ReadOnlyMemory<byte> HeadersDocument) : IResourceTelemetry
{
    internal static void Apply(ResourceTelemetryInjection? telemetry, IDictionary<string, string> environment) =>
        ((IResourceTelemetry?)telemetry).ApplyEnvironment(environment);
}
