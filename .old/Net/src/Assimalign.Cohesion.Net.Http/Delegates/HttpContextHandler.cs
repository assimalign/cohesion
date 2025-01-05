using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http;

/// <summary>
/// 
/// </summary>
/// <param name="context"></param>
/// <param name="next"></param>
/// <returns></returns>
public delegate Task HttpContextHandler(IHttpContext context, HttpContextHandler next, CancellationToken cancellationToken);