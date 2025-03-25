using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Web.Http;

public interface IHttpQueryCollection : IDictionary<HttpQueryKey, HttpQueryValue>
{
}
