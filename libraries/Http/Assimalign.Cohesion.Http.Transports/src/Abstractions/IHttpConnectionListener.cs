using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

using Transports;

/// <summary>
/// 
/// </summary>
public interface IHttpConnectionListener : ITransport
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IHttpConnection> AcceptOrListenAsync(CancellationToken cancellationToken = default);
}


