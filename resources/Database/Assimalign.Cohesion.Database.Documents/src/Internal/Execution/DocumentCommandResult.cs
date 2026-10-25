using System.Collections.Generic;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Internal;

[global::System.Diagnostics.DebuggerDisplay("DocumentCommandResult")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Internal.DocumentCommandResult.DebugView))]
internal sealed class DocumentCommandResult : DatabaseResult
{
    internal static DocumentCommandResult Success { get; } = new();

    private readonly long _affectedCount;

    internal DocumentCommandResult(long affectedCount = 0) => _affectedCount = affectedCount;

    public override DatabaseResultStatus Status => DatabaseResultStatus.Success;
    public override long AffectedCount => _affectedCount;
    public override IReadOnlyList<Diagnostic> Diagnostics => [];

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Documents.Internal.DocumentCommandResult _target;

        public DebugView(global::Assimalign.Cohesion.Database.Documents.Internal.DocumentCommandResult value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Execution.DatabaseResultStatus Status => _target.Status;
        public long AffectedCount => _target.AffectedCount;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Types.Diagnostic> Diagnostics => _target.Diagnostics;
    }
}
