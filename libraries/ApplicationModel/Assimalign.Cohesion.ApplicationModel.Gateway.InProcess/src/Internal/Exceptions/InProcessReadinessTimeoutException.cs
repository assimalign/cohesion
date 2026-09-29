using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.Internal;

internal sealed class InProcessReadinessTimeoutException : TimeoutException
{
    internal InProcessReadinessTimeoutException(
        ResourceName resourceName,
        TimeSpan readinessBudget,
        Exception innerException)
        : base(
            $"Resource '{resourceName}' exceeded its in-process readiness budget of "
            + $"'{readinessBudget}'.",
            innerException)
    {
    }
}
