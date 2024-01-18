
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Udt;

using Assimalign.Cohesion.Net.Hosting;

public sealed class UdtServerBuilder : IHostServerBuilder
{
    IHostServer IHostServerBuilder.Build()
    {
        return new UdtServer(default);
    }
}
