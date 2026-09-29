using System;

namespace Assimalign.Cohesion.Database.Protocol;

/// <summary>An immutable endpoint-selected set of model message identifiers.</summary>
/// <remarks>One channel binds one family for its entire lifetime. Identifiers 5 through 9
/// and 64 through 255 belong to models; all other identifiers are reserved by the core.</remarks>
public sealed class ProtocolMessageFamily
{
    private readonly bool[] _types = new bool[256];

    /// <summary>Defines the identifiers understood by one model endpoint.</summary>
    /// <param name="name">The model family's diagnostic name.</param>
    /// <param name="messageTypes">The family's unique message identifiers.</param>
    /// <exception cref="ArgumentException">The name is empty, or an identifier is duplicated or reserved.</exception>
    /// <exception cref="ArgumentNullException">The identifiers are null.</exception>
    public ProtocolMessageFamily(string name, params byte[] messageTypes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(messageTypes);
        Name = name;
        foreach (byte type in messageTypes)
        {
            if (type is not (>= 5 and <= 9 or >= 64) || _types[type])
            {
                throw new ArgumentException($"Message identifier {type} is reserved or duplicated.", nameof(messageTypes));
            }
            _types[type] = true;
        }
    }

    /// <summary>Gets the endpoint family's diagnostic name.</summary>
    public string Name { get; }

    /// <summary>Checks whether this family or the shared core defines an identifier.</summary>
    /// <param name="type">The identifier to check.</param>
    /// <returns>True for a defined core or family identifier.</returns>
    public bool Supports(ProtocolMessageType type) =>
        type is ProtocolMessageType.Startup or ProtocolMessageType.Authenticate or
        ProtocolMessageType.AuthenticateResponse or ProtocolMessageType.Ready or
        ProtocolMessageType.Error or ProtocolMessageType.Ping or ProtocolMessageType.Pong or
        ProtocolMessageType.Terminate || _types[(byte)type];
}
