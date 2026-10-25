using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Protocol;

/// <summary>An immutable capability and the additional frame identifiers it gates.</summary>
public sealed class ProtocolCapability
{
    /// <summary>Defines a capability understood by an endpoint.</summary>
    /// <param name="name">An ordinal, lowercase ASCII name; model capabilities begin with the lowercase family name and a dot.</param>
    /// <param name="messageTypes">Additional identifiers requiring this capability.</param>
    /// <exception cref="ProtocolException">The name is malformed.</exception>
    /// <exception cref="ArgumentNullException">The identifiers are null.</exception>
    public ProtocolCapability(string name, params byte[] messageTypes)
    {
        Internal.ProtocolCapabilityCodec.ValidateName(name);
        ArgumentNullException.ThrowIfNull(messageTypes);
        Name = name;
        MessageTypes = Array.AsReadOnly((byte[])messageTypes.Clone());
    }

    /// <summary>Gets the capability name.</summary>
    public string Name { get; }

    /// <summary>Gets the frame identifiers gated by the capability.</summary>
    public IReadOnlyList<byte> MessageTypes { get; }

    /// <summary>Gets the shared structured statement diagnostics capability.</summary>
    public static ProtocolCapability Diagnostics { get; } = new("diagnostics", (byte)ProtocolMessageType.Diagnostics);

    /// <summary>Gets the shared Execute status and ROLLBACK reset contract, using existing Execute and Ready identifiers.</summary>
    public static ProtocolCapability TransactionStatus { get; } = new("transaction-status");
}
