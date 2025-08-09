
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

using Assimalign.Cohesion.Transports;

public interface IHttpConnectionFactory
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="transportConnection"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IHttpConnection> CreateAsync(ITransportConnection transportConnection, CancellationToken cancellationToken = default);
}
