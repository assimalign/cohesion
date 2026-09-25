using System.Collections.Generic;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed class DocumentCommandResult : QueryResult
{
    internal static DocumentCommandResult Success { get; } = new();

    private DocumentCommandResult() { }

    public override QueryResultStatus Status => QueryResultStatus.Success;
    public override long AffectedCount => 0;
    public override IReadOnlyList<Diagnostic>? Diagnostics => null;
}
