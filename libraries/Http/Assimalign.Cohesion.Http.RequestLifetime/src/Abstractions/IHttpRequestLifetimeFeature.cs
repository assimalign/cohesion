using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Http;

public interface IHttpRequestLifetimeFeature : IHttpFeature
{
    IHttpRequestLifetime RequestLifetime { get; }
}
