using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

/// <summary>
/// 
/// </summary>
/// <param name="context"></param>
/// <returns></returns>
public delegate Task TransportMiddleware(ITransportContext context);
