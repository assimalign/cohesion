using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting;

public static class HostingExtensions
{
    public static IHostBuilder AddSecretStore(this IHostBuilder builder)
    {
        return builder;
    }
}