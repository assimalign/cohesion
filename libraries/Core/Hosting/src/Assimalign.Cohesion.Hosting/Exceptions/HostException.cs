using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting;

public abstract class HostException : Exception
{
	public HostException(string message): base(message) { }
	public HostException(string message, Exception innerException)  : base(message, innerException) { }
}
