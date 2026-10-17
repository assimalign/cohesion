using System.Collections.Generic;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Tests;

internal sealed class RejectedGraphResult : DatabaseResult
{
    internal RejectedGraphResult(IReadOnlyList<Diagnostic> diagnostics)
    {
        Diagnostics = diagnostics;
    }

    public override DatabaseResultStatus Status => DatabaseResultStatus.Error;
    public override long AffectedCount => 0;
    public override IReadOnlyList<Diagnostic> Diagnostics { get; }
}
