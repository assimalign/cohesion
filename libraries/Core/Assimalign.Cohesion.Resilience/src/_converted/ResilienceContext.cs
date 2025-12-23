using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Assimalign.Cohesion.Resilience;

public class ResilienceContext : IResilienceContext
{
    public string? OperationKey { get; set; }
    public CancellationToken CancellationToken { get; set; } = CancellationToken.None;
    public bool ContinueOnCapturedContext { get; set; }
    //public ResilienceProperties Properties => throw new NotImplementedException();
}
