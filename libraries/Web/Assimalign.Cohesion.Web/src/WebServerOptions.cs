using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web;

using Assimalign.Cohesion.Net.Transports;

public sealed class WebServerOptions
{
	public WebServerOptions()
	{
		Transports = new();
	}
    public List<ITransport> Transports { get; }

}
