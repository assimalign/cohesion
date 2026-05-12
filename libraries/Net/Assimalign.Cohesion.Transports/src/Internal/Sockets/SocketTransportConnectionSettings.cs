using System.Net.Sockets;

namespace Assimalign.Cohesion.Transports.Internal;

internal sealed class SocketTransportConnectionSettings
{
    public bool IsServer { get; set; }
    public Socket Socket { get; set; } = default!;
    public SocketTransportPipeOptionsContext PipeOptions { get; init; } = default!;
    public bool WaitForDataBeforeAllocatingBuffer { get; set; }
}
