using System;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.Database.Protocol;

/// <summary>An immutable endpoint-selected set of model message identifiers.</summary>
/// <remarks>One channel binds one family for its entire lifetime. Identifiers 5 through 9
/// and 64 through 255 belong to models; all other identifiers are reserved by the core.</remarks>
[global::System.Diagnostics.DebuggerDisplay("ProtocolMessageFamily")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Protocol.ProtocolMessageFamily.DebugView))]
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
                throw new ArgumentException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ProtocolMessageFamilyArgument, type), nameof(messageTypes));
            }
            _types[type] = true;
        }
    }

    /// <summary>Defines an endpoint with optional, scoped capabilities.</summary>
    /// <param name="name">The endpoint family name.</param>
    /// <param name="capabilities">Capabilities supported by this endpoint.</param>
    /// <param name="messageTypes">The endpoint message identifiers.</param>
    /// <param name="resetStatement">The statement that clears retained session state, or null for a stateless family.</param>
    /// <exception cref="ArgumentException">A capability is duplicated, wrongly scoped, or gates an undefined identifier, or the reset statement is empty.</exception>
    /// <exception cref="ArgumentNullException">The capabilities are null.</exception>
    public ProtocolMessageFamily(string name, IReadOnlyList<ProtocolCapability> capabilities, string? resetStatement, params byte[] messageTypes)
        : this(name, messageTypes)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (resetStatement is not null) { ArgumentException.ThrowIfNullOrWhiteSpace(resetStatement); }
        ResetStatement = resetStatement;
        var names = new HashSet<string>(StringComparer.Ordinal);
        var gated = new HashSet<byte>();
        foreach (var capability in capabilities)
        {
            ArgumentNullException.ThrowIfNull(capability);
            bool core = ReferenceEquals(capability, ProtocolCapability.Diagnostics) || ReferenceEquals(capability, ProtocolCapability.TransactionStatus);
            if (!names.Add(capability.Name) || (!core && !capability.Name.StartsWith(name.ToLowerInvariant() + ".", StringComparison.Ordinal)))
            {
                throw new ArgumentException(ErrorMessages.CapabilityListInvalid, nameof(capabilities));
            }
            foreach (byte type in capability.MessageTypes)
            {
                if ((!core && !_types[type]) || !gated.Add(type))
                {
                    throw new ArgumentException(ErrorMessages.CapabilityListInvalid, nameof(capabilities));
                }
            }
        }
        if (capabilities.Count > Internal.ProtocolCapabilityCodec.MaxCount) { throw new ArgumentException(ErrorMessages.CapabilityListInvalid, nameof(capabilities)); }
        Capabilities = Array.AsReadOnly(capabilities.ToArray());
    }

    /// <summary>Gets the endpoint's supported capabilities.</summary>
    public IReadOnlyList<ProtocolCapability> Capabilities { get; } = Array.Empty<ProtocolCapability>();

    /// <summary>Gets the statement that clears retained session state, or null when none is supported.</summary>
    public string? ResetStatement { get; }

    /// <summary>Gets the endpoint family's diagnostic name.</summary>
    public string Name { get; }

    /// <summary>Checks whether this family or the shared core defines an identifier.</summary>
    /// <param name="type">The identifier to check.</param>
    /// <returns>True for a defined core or family identifier.</returns>
    public bool Supports(ProtocolMessageType type) =>
        type is ProtocolMessageType.Startup or ProtocolMessageType.Authenticate or
        ProtocolMessageType.AuthenticateResponse or ProtocolMessageType.Ready or
        ProtocolMessageType.Error or ProtocolMessageType.Ping or ProtocolMessageType.Pong or
        ProtocolMessageType.Terminate or ProtocolMessageType.Diagnostics || _types[(byte)type];

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Protocol.ProtocolMessageFamily _target;

        public DebugView(global::Assimalign.Cohesion.Database.Protocol.ProtocolMessageFamily value)
        {
            _target = value;
        }

        public string Name => _target.Name;
        public string? ResetStatement => _target.ResetStatement;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Protocol.ProtocolCapability> Capabilities => _target.Capabilities;
    }
}
