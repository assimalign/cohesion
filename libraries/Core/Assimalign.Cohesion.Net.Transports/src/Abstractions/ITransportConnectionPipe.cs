using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Transports;

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
    /// <returns></returns>
    ValueTask<ReadResult> ReadAsync();
    /// <summary>
    /// 
    /// </summary>
    /// <param name="buffer"></param>
    /// <returns></returns>
    ValueTask<FlushResult> WriteAsync(ReadOnlyMemory<byte> buffer);
}
