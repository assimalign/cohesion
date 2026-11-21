using System;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Exposes what the SASL exchange of an AMQP connection established (AMQP 1.0 Part 5, section 5.3).
/// </summary>
/// <remarks>
/// An AMQP connection that authenticated with SASL registers this feature under <see cref="FeatureName"/>
/// before its listener or factory hands it out; read it with the <c>connection.AmqpSasl</c> extension
/// property, which returns <see langword="null"/> for a connection that skipped SASL.
/// </remarks>
public interface IAmqpSaslConnectionInfo : IConnectionFeature
{
    /// <summary>
    /// The name of the AMQP SASL connection feature.
    /// </summary>
    public const string FeatureName = "AmqpSasl";

    /// <summary>
    /// Gets the mechanism the client selected.
    /// </summary>
    AmqpSymbol Mechanism { get; }

    /// <summary>
    /// Gets the host name the client's <c>sasl-init</c> named, or <see langword="null"/>.
    /// </summary>
    string? HostName { get; }

    /// <summary>
    /// Gets the identity the mechanism authenticated, on a listening endpoint; <see langword="null"/> for an
    /// anonymous client and on a connecting endpoint.
    /// </summary>
    string? Identity { get; }

    /// <summary>
    /// Gets the additional data of the server's <c>sasl-outcome</c>; empty when it sent none.
    /// </summary>
    ReadOnlyMemory<byte> AdditionalData { get; }
}
