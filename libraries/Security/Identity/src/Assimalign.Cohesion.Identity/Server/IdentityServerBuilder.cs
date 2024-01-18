
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Identity;

using Assimalign.Cohesion.Net.Hosting;

public sealed class IdentityServerBuilder : IHostServerBuilder
{
    IHostServer IHostServerBuilder.Build()
    {
        throw new NotImplementedException();
    }
}
