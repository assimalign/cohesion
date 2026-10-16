using System.Collections.Generic;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed class DocumentCommandResult : DatabaseResult
{
    internal static DocumentCommandResult Success { get; } = new();

    private DocumentCommandResult() { }

    public override DatabaseResultStatus Status => DatabaseResultStatus.Success;
    public override long AffectedCount => 0;
    public override IReadOnlyList<Diagnostic>? Diagnostics => null;
}
