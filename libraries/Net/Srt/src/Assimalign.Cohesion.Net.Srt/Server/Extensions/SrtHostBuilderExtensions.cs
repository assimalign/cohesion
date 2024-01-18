
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Srt;

using Assimalign.Cohesion.Net.Hosting;

public static class SrtHostBuilderExtensions
{
    public static IHostBuilder AddSrtServer(this IHostBuilder builder, Action<SrtServerBuilder> configure)
    {


        return builder;
    }
}
