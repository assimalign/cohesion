using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web;

using Http;

/// <summary>
/// 
/// </summary>
/// <param name="context"></param>
/// <returns></returns>
public delegate Task WebApplicationMiddleware(IHttpContext context);