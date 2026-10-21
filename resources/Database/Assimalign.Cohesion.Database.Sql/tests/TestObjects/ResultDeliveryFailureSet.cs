using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

internal sealed class ResultDeliveryFailureSet : DatabaseResultSet
{
    private readonly string _failure;

    internal ResultDeliveryFailureSet(string failure) { _failure = failure; }

    internal int RowsVisited { get; private set; }
    internal bool Disposed { get; private set; }
    internal TaskCompletionSource EnumerationBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ReleaseEnumeration { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override DatabaseResultStatus Status => DatabaseResultStatus.Success;
    public override long AffectedCount => -1;
    public override IReadOnlyList<Diagnostic> Diagnostics => [];
    public override IReadOnlyList<DatabaseColumn> Columns => _failure == "header"
        ? throw new DatabaseTypeException("Injected column failure.")
        : [new() { Name = "value", Ordinal = 0, Type = DatabaseType.String }];

    public override async IAsyncEnumerable<DatabaseRow> GetRowsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RowsVisited++;
        yield return new SqlMaterializedRow(["partial"]);
        await Task.Yield();
        if (_failure is "cancel" or "blocked")
        {
            EnumerationBlocked.SetResult();
            await ReleaseEnumeration.Task.WaitAsync(cancellationToken);
        }
        if (_failure == "buffered")
        {
            string value = new('x', 1048576);
            for (int index = 0; index < 64; index++)
            {
                RowsVisited++;
                yield return new SqlMaterializedRow([value]);
            }
            yield break;
        }
        if (_failure == "enumeration")
        {
            throw new DatabaseException("Injected enumeration failure.",
                [new Diagnostic { Code = "COHSQLE041", Message = "Injected enumeration failure.", Severity = DiagnosticSeverity.Error }]);
        }
        if (_failure == "uncoded") { throw new DatabaseException("Injected uncoded failure."); }
        if (_failure == "dispose") { yield break; }
        RowsVisited++;
        yield return new SqlMaterializedRow([_failure switch
        {
            "unicode" => "\uD800",
            "oversized" => new string('x', (int)ProtocolFrameHeader.MaxPayloadLength),
            "binary" => new byte[ProtocolFrameHeader.MaxPayloadLength / 2],
            _ => new object(),
        }]);
    }

    public override ValueTask DisposeAsync()
    {
        Disposed = true;
        return _failure == "dispose" ? ValueTask.FromException(new DatabaseTypeException("Injected disposal failure.")) : ValueTask.CompletedTask;
    }
}
