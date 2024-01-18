using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http;

public interface IHttpHeaderCollection : IDictionary<HttpHeaderKey, HttpHeaderValue>
{



    HttpHeaderValue? Accepts { get; }
    HttpHeaderValue? ContentType { get; }
    HttpHeaderValue? ContentLength { get; }
    HttpHeaderValue? TransferEncoding { get; }
    HttpHeaderValue? Connection { get; }
}
