using System;

using Xunit;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

/// <summary>Reports measurement probes as skipped unless explicitly enabled.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class DatabaseProbeTheoryAttribute : TheoryAttribute
{
    public DatabaseProbeTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("COHESION_DB_PROBES") != "1")
        {
            Skip = "Measurement probe excluded from default CI; set COHESION_DB_PROBES=1 to run it.";
        }
    }
}
