using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Web.Routing;

using Assimalign.Cohesion.Http;

public interface IRouterFeature : IHttpFeature
{
    IRouter Router { get; }
    IRouterBuilder Builder { get; }
}
