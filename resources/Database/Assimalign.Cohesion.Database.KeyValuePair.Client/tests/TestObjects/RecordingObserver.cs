using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.KeyValuePair.Client.Tests;

/// <summary>
/// A telemetry observer that records the executing / executed / failed callbacks
/// it receives, for asserting the telemetry hook fires around commands.
/// </summary>
internal sealed class RecordingObserver : IKeyValueClientObserver
{
    public List<string> Executing { get; } = new();

    public List<(string CommandText, long RowCount, long AffectedCount)> Executed { get; } = new();

    public List<(string CommandText, KeyValueClientErrorKind Kind)> Failed { get; } = new();

    public void OnExecuting(string commandText, int parameterCount) => Executing.Add(commandText);

    public void OnExecuted(string commandText, long rowCount, long affectedCount, TimeSpan elapsed)
        => Executed.Add((commandText, rowCount, affectedCount));

    public void OnFailed(string commandText, KeyValueClientException exception, TimeSpan elapsed)
        => Failed.Add((commandText, exception.Kind));
}
