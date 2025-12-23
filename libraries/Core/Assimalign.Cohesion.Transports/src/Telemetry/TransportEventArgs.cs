namespace Assimalign.Cohesion.Transports;

public readonly struct TransportEventArgs<TArgs>
{
    public TransportEventArgs(
        TransportId transportId, 
        TransportKind kind, 
        TransportProtocol protocol, 
        TArgs arguments)
    {
        TransportId = transportId;
        Kind = kind;
        Protocol = protocol;
        Arguments = arguments;
    }

    /// <summary>
    /// 
    /// </summary>
    public TransportId TransportId { get; }

    /// <summary>
    /// Specifies whether the transport is a client or server.
    /// </summary>
    public TransportKind Kind { get; }

    /// <summary>
    /// The underlying network protocol of the transport.
    /// </summary>
    public TransportProtocol Protocol { get; }

    /// <summary>
    /// 
    /// </summary>
    public TArgs Arguments { get; }
}
