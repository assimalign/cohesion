using Assimalign.Cohesion.Hosting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

public abstract class Application : IApplication
{
    Task IHostService.StartAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    Task IHostService.StopAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}
