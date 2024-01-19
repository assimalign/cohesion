using Assimalign.Cohesion.Hosting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

internal class HttpServerState : IHostServerState
{
    public string ServerName { get; set; }
    public HostServerStatus Status { get; set; }
}
