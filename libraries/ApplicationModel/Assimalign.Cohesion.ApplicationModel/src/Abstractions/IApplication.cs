using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

using Assimalign.Cohesion.Hosting;

public interface IApplication<TContext> : IHost where TContext : IApplicationContext
{
    new TContext Context { get; }
}
