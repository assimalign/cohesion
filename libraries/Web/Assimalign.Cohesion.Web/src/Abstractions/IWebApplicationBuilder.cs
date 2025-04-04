using Assimalign.Cohesion.Transports;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web;

public interface IWebApplicationBuilder
{

    IWebApplicationBuilder UseTransport(ITransport transport);
    IWebApplication Build();
}
