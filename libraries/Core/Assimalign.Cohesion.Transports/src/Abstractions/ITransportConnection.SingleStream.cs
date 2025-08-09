using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

/// <summary>
/// A single logical stream directly mapped to a transport (e.g., a TCP socket).
/// </summary>
public interface ISingleStreamTransportConnection : ITransportConnection
{
    /// <summary>
    /// Opens the point to point connection that allows reading and writing.
    /// </summary>
    /// <returns></returns>
    ITransportConnectionContext Open();

    /// <summary>
    /// Opens the point to point connection that allows reading and writing.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    ValueTask<ITransportConnectionContext> OpenAsync(CancellationToken cancellationToken = default);
}
