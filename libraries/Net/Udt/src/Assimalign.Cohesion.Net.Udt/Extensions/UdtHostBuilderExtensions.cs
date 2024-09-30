using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Udt;

using Assimalign.Cohesion.Hosting;

public static class UdtHostBuilderExtensions
{
    /// <summary>
    /// 
    /// </summary>
    /// <remarks>
    /// UDT is best used for high-speed file transfer.
    /// </remarks>
    /// <param name="builder"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static IHostBuilder AddUdtServer(this IHostBuilder builder, Action<UdtServerBuilder> configure)
    {
        if (configure is null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        var serverBuilder = new UdtServerBuilder();

        configure.Invoke(serverBuilder);

        return builder.AddService(((IHostServiceBuilder)serverBuilder).Build());
    }
}
