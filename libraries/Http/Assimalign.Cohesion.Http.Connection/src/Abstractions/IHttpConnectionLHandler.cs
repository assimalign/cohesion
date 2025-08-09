using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// 
/// </summary>
public interface IHttpContextHandler
{

    Task OnAcceptAsync(IHttpConnection connection, CancellationToken cancellationToken = default);



    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    //Task<IHttpConnection> AcceptAsync(CancellationToken cancellationToken = default);
}
