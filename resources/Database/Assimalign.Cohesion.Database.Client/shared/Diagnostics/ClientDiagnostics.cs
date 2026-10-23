using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Types;

// Deviates from the repo RootNamespace rule per design decision: each client compiles
// this stateless snapshot algorithm in its owning namespace; no instances cross assemblies.
namespace Assimalign.Cohesion.Database.Client.Internal;

internal static class ClientDiagnostics
{
    internal static IReadOnlyList<Diagnostic> Snapshot(IEnumerable<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        return Array.AsReadOnly(diagnostics.OrderBy(diagnostic => diagnostic.Start is null)
            .ThenBy(diagnostic => diagnostic.Start).ToArray());
    }
}
