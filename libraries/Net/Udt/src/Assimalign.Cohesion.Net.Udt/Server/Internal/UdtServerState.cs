
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Udt.Internal;

using Assimalign.Cohesion.Net.Hosting;

internal class UdtServerState : IHostServerState
{
    public string ServerName { get; set; }
    public HostServerStatus Status => throw new NotImplementedException();
}
