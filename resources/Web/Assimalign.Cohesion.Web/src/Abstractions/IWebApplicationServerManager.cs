using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Web;

public interface IWebApplicationServerManager
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="server"></param>
    /// <returns></returns>
    IWebApplicationBuilder AddServer(IWebApplicationServer server);
}
