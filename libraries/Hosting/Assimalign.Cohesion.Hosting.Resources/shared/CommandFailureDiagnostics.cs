using System;
using System.Collections;
using System.Runtime.CompilerServices;

// Deviates from the RootNamespace rule per shared-source design: this stateless helper keeps
// its owning namespace. Only BCL objects and the public ResourceCommand cross the boundary.
namespace Assimalign.Cohesion.Hosting.Resources.Internal;

internal static class CommandFailureDiagnostics
{
    private const string dataKey = "Assimalign.Cohesion.Hosting.Resources.ReportedCommandFailures";

    internal static void MarkReported(Exception exception, ResourceCommand invocation)
    {
        try
        {
            lock (exception)
            {
                IDictionary data = exception.Data;
                if (data.IsReadOnly)
                {
                    return;
                }

                if (data[dataKey] is not ConditionalWeakTable<ResourceCommand, object> reported)
                {
                    reported = new ConditionalWeakTable<ResourceCommand, object>();
                    data[dataKey] = reported;
                }

                // An exception may be reused concurrently. Track individual invocations without
                // retaining their command payloads, and without process-global shared state.
                reported.AddOrUpdate(invocation, string.Empty);
            }
        }
        catch (Exception)
        {
            // Exception.Data is virtual and can execute arbitrary application code. Diagnostics
            // must never replace the original failure, including for a read-only/custom carrier.
        }
    }

    internal static bool TryConsume(Exception exception, ResourceCommand? invocation)
    {
        if (invocation is null)
        {
            return false;
        }

        try
        {
            lock (exception)
            {
                IDictionary data = exception.Data;
                return data[dataKey] is ConditionalWeakTable<ResourceCommand, object> reported &&
                    reported.Remove(invocation);
            }
        }
        catch (Exception)
        {
            // A custom exception carrier must not change the HTTP response or hide the fault.
            return false;
        }
    }
}
