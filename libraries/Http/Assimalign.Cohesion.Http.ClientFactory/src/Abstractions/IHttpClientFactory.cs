using System;
using System.Collections.Generic;
using System.Net.Http;

namespace Assimalign.Cohesion.Http;

public interface IHttpClientFactory
{
    HttpClient Create(string name);
}
