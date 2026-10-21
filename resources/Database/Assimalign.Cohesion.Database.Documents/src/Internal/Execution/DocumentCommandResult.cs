using System.Collections.Generic;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed class DocumentCommandResult : DatabaseResult
{
    internal static DocumentCommandResult Success { get; } = new();

    private readonly long _affectedCount;

    internal DocumentCommandResult(long affectedCount = 0) => _affectedCount = affectedCount;

    public override DatabaseResultStatus Status => DatabaseResultStatus.Success;
    public override long AffectedCount => _affectedCount;
    public override IReadOnlyList<Diagnostic> Diagnostics => [];
}
