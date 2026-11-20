using System;

namespace Assimalign.Cohesion.Amqp.Tests;

/// <summary>
/// An <see cref="IAmqpSaslConnectionInfo"/> a test sets on a connection's features.
/// </summary>
internal sealed class TestSaslConnectionInfo : IAmqpSaslConnectionInfo
{
    public string Name => IAmqpSaslConnectionInfo.FeatureName;

    public AmqpSymbol Mechanism { get; init; } = new("PLAIN");

    public string? HostName { get; init; }

    public string? Identity { get; init; }

    public ReadOnlyMemory<byte> AdditionalData { get; init; }
}
