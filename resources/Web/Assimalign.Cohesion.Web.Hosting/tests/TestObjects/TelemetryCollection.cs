using Xunit;

namespace Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

/// <summary>
/// Serializes the tests that observe the server's telemetry: activity and meter listeners are
/// process-wide, so a request another test sends concurrently would show up in them, and a listener
/// one test attaches would make another test's "no listener" request create an activity.
/// </summary>
[CollectionDefinition(nameof(TelemetryCollection), DisableParallelization = true)]
public sealed class TelemetryCollection
{
}
