using Xunit;

namespace Assimalign.Cohesion.Web.Server.Tests.TestObjects;

/// <summary>
/// Serializes tests that observe hosting telemetry: activity, meter, and event-source listeners
/// are process-wide, including the control-plane events on the hosting source. A concurrent request
/// would show up in them, and a listener could affect another test's "no listener" assertions.
/// </summary>
[CollectionDefinition(nameof(TelemetryCollection), DisableParallelization = true)]
public sealed class TelemetryCollection
{
}
