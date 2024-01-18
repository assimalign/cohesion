using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Hosting.Tests;


public class TestServerState : IHostServerState
{
    public HostServerStatus Status { get; set; }
}

