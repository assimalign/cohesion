using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.KeyValuePair.Client.Tests;

/// <summary>
/// A telemetry observer that records the executing / executed / failed callbacks
/// it receives, for asserting the telemetry hook fires around commands.
/// </summary>
internal sealed class RecordingObserver : KeyValueClientObserver
{
    public List<string> Executing { get; } = new();

    public List<(string CommandText, long RowCount, long AffectedCount)> Executed { get; } = new();

    public List<(string CommandText, KeyValueClientErrorKind Kind)> Failed { get; } = new();

    protected override void OnExecuting(string commandText, int parameterCount) => Executing.Add(commandText);

    protected override void OnExecuted(string commandText, long rowCount, long affectedCount, TimeSpan elapsed)
        => Executed.Add((commandText, rowCount, affectedCount));

    protected override void OnFailed(string commandText, KeyValueClientException exception, TimeSpan elapsed)
        => Failed.Add((commandText, exception.Kind));
}
