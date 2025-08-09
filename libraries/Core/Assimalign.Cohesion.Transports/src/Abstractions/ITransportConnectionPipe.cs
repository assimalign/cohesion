using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

/// <summary>
/// Allows for data to be sent and received from a <see cref="ITransportConnection"/>.
/// </summary>
/// <remarks>
/// Is derived from <see cref="IDuplexPipe"/> which can be used to write to and read from.
/// </remarks>
public interface ITransportConnectionPipe : IDuplexPipe
{
    /// <summary>
    /// Converts the <see cref="ITransportConnectionPipe"/> to a <see cref="Stream"/>.
    /// </summary>
    /// <returns></returns>
    Stream GetStream();

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    ValueTask<ReadResult> PeekAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="buffer"></param>
    /// <returns></returns>
    ValueTask<FlushResult> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default);
}
