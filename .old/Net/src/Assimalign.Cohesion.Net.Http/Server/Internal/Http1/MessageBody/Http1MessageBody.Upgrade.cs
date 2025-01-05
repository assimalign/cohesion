using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

internal class Http1UpgradeMessageBody : Http1MessageBody
{
    public Http1UpgradeMessageBody(PipeReader reader) : base(reader)
    {
    }
}
