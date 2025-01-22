using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Transports;


/// <summary>
/// 
/// </summary>
/// <param name="context"></param>
/// <param name="next"></param>
/// <returns></returns>
public delegate Task TransportMiddleware(ITransportContext context, TransportMiddlewareHandler next);
