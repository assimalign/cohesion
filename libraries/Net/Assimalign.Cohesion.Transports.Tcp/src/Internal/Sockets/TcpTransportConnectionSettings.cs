using System.Net.Sockets;

namespace Assimalign.Cohesion.Transports.Internal;

internal sealed class TcpTransportConnectionSettings
{
    public TransportId TransportId { get; set; }
    public TransportPipeline<TcpTransportConnectionContext>? Pipeline { get; set; }
    public bool IsServer { get; set; }
    public Socket Socket { get; set; } = default!;
    public SocketTransportPipeOptionsContext PipeOptions { get; init; } = default!;
    public bool WaitForDataBeforeAllocatingBuffer { get; set; }
}
