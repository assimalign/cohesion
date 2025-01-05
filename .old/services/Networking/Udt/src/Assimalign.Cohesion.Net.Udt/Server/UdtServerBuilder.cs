
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Udt;

using Assimalign.Cohesion.Hosting;

public sealed class UdtServerBuilder : IHostServiceBuilder
{
    IHostService IHostServiceBuilder.Build()
    {
        return new UdtServer(default);
    }
}
