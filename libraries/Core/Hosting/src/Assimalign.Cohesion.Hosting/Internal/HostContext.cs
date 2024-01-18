using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Internal;

internal sealed class HostContext
{
    public HostServerStateCallbackAsync ServerStateCallback { get; init; }
    public IEnumerable<IHostServer> Servers { get; init; }
    public TimeSpan StateCheckInterval { get; init; }
    public bool ThrowExceptionOnServerStartFailure { get; init; }
}
