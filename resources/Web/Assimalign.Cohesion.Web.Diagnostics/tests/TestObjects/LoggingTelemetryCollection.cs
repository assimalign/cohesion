using Xunit;

namespace Assimalign.Cohesion.Web.Diagnostics.Tests.TestObjects;

// Activity listeners are process-wide; no-listener cases must run in isolation.
[CollectionDefinition(nameof(LoggingTelemetryCollection), DisableParallelization = true)]
public sealed class LoggingTelemetryCollection
{
}
