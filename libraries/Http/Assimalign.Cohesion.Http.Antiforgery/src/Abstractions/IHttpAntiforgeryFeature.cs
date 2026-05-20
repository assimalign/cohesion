using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Http;

public interface IHttpAntiforgeryFeature : IHttpFeature
{
    IHttpAntiforgery Antiforgery { get; }
}
