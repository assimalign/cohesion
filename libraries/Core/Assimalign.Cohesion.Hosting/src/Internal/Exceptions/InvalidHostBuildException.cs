using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Internal;

internal sealed class InvalidHostBuildException : HostException
{
    public InvalidHostBuildException(string message) : base(message)
    {
    }
}
