
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.WebSockets.Internal;

using Assimalign.Cohesion.Net.Hosting;

internal class WebSocketServerState : IHostServerState
{
    public string ServerName { get; set; }
    public HostServerStatus Status { get; set; }
}
