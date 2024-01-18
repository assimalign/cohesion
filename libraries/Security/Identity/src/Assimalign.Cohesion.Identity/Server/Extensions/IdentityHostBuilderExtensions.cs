
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Identity;

using Assimalign.Cohesion.Net.Hosting;

public static class IdentityHostBuilderExtensions
{
    public static IHostBuilder AddIdentityServer(this IHostBuilder builder, Action<IdentityServerBuilder> configure)
    {


        return builder;
    }
}
