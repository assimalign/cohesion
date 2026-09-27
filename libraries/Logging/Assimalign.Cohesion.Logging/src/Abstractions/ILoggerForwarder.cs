using System;

namespace Assimalign.Cohesion.Logging;

/// <summary>
/// A component that writes entries <em>into</em> a logger factory from a source outside the
/// logging pipeline, such as runtime event sources.
/// </summary>
/// <remarks>
/// <para>
/// A provider is a sink the factory writes to; a forwarder is the reverse. It receives the built
/// factory when it is created and writes through the factory's loggers, so the factory's filter
/// rules, enrichers, and providers apply to forwarded entries exactly as they do to any other.
/// </para>
/// <para>
/// A forwarder registered with <see cref="ILoggerFactoryBuilder.AddForwarder"/> is owned by the
/// factory: it is created as the last step of factory construction, before any application code
/// runs against the factory, and disposed when the factory is disposed, before its providers.
/// Forwarding is active for the forwarder's whole lifetime; there is no separate start or stop.
/// </para>
/// <para>
/// Implementations must be thread-safe and must never throw into the code whose activity they
/// forward.
/// </para>
/// </remarks>
public interface ILoggerForwarder : IDisposable
{
}
