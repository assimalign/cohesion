using System;

using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// Records the SASL exchange a connection completed, as the connection feature
/// <see cref="IAmqpSaslConnectionInfo"/>.
/// </summary>
internal sealed class AmqpSaslConnectionInfo : IAmqpSaslConnectionInfo
{
    public AmqpSaslConnectionInfo(AmqpSymbol mechanism, string? hostName, string? identity, ReadOnlyMemory<byte> additionalData)
    {
        Mechanism = mechanism;
        HostName = hostName;
        Identity = identity;
        AdditionalData = additionalData;
    }

    public string Name => IAmqpSaslConnectionInfo.FeatureName;

    public AmqpSymbol Mechanism { get; }

    public string? HostName { get; }

    public string? Identity { get; }

    public ReadOnlyMemory<byte> AdditionalData { get; }
}
