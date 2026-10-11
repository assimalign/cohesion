using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>Isolates the QUIC driver's process-wide stream gauge from other HTTP/3 tests.</summary>
[CollectionDefinition("Http3StreamLifetime", DisableParallelization = true)]
public sealed class Http3StreamLifetimeCollection
{
}
