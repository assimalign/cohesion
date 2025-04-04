using Assimalign.Cohesion.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web;

public interface IWebApplicationPipeline
{
    Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken);
}
