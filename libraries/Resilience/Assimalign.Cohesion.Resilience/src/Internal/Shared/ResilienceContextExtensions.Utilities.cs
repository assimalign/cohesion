using System;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Resilience.Internal;

internal static class ResilienceContextExtensions
{
    extension(IResilienceContext context)
    {
        public bool IsPipelineCancelled([NotNullWhen(true)] out OperationCanceledException? exception)
        {
            exception = default!;
            if (context.CancellationToken.IsCancellationRequested)
            {
                exception = new OperationCanceledException($"The pipeline operation: '{context.OperationKey}' has been cancelled.", context.CancellationToken);
            }
            return exception is not null;
        }
    }
}
