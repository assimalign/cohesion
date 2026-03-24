
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Internal;

using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.DependencyInjection;

internal class WebApplicationContext
{
    public required IConfigurationRoot Configuration { get; init; }
    public required IServiceProvider Services { get; init; }
}
