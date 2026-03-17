namespace Assimalign.Cohesion.Transports;

/// <summary>
/// Placeholder for a future endpoint-aware datagram multiplex abstraction.
/// </summary>
/// <remarks>
/// Intentionally commented out until the envelope and endpoint contracts are finalized.
/// </remarks>
// public readonly record struct DatagramReceiveResult(
//     EndPoint RemoteEndPoint,
//     ReadOnlyMemory<byte> Datagram);
//
// public interface IDatagramMultiplexTransportConnection : ITransportConnection
// {
//     ValueTask<DatagramReceiveResult> ReceiveAsync(CancellationToken cancellationToken = default);
//
//     ValueTask<int> SendAsync(
//         EndPoint remoteEndPoint,
//         ReadOnlyMemory<byte> datagram,
//         CancellationToken cancellationToken = default);
// }
