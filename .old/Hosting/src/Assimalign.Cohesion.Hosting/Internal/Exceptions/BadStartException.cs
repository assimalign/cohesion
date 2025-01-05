using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Internal;

internal class BadStartException : HostException
{
    public BadStartException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
